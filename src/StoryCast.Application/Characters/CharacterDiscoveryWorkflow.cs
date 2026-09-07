using System.Diagnostics;
using StoryCast.Application.Attribution;
using StoryCast.Application.Preparation;
using StoryCast.Domain.Books;

namespace StoryCast.Application.Characters;

/// <summary>
/// Performs resumable character discovery across an ordered book manuscript.
/// </summary>
public sealed class CharacterDiscoveryWorkflow
{
    private readonly IChapterTextPreparer preparer;
    private readonly ICharacterDiscoveryService discoveryService;
    private readonly CharacterRegistryMerger merger;
    private readonly ICharacterRegistryStore registryStore;

    /// <summary>
    /// Initializes the character-discovery workflow.
    /// </summary>
    /// <param name="preparer">
    /// The deterministic chapter text preparer.
    /// </param>
    /// <param name="discoveryService">
    /// The character-discovery service.
    /// </param>
    /// <param name="merger">
    /// The deterministic character-registry merger.
    /// </param>
    /// <param name="registryStore">
    /// The persistent character-registry store.
    /// </param>
    public CharacterDiscoveryWorkflow(
        IChapterTextPreparer preparer,
        ICharacterDiscoveryService discoveryService,
        CharacterRegistryMerger merger,
        ICharacterRegistryStore registryStore)
    {
        this.preparer =
            preparer ??
            throw new ArgumentNullException(nameof(preparer));

        this.discoveryService =
            discoveryService ??
            throw new ArgumentNullException(nameof(discoveryService));

        this.merger =
            merger ??
            throw new ArgumentNullException(nameof(merger));

        this.registryStore =
            registryStore ??
            throw new ArgumentNullException(nameof(registryStore));
    }

    /// <summary>
    /// Discovers and persists speaking characters across a complete book.
    /// </summary>
    /// <param name="book">The loaded book project.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <param name="force">
    /// When true, processes chapters even when their source hashes
    /// have already been recorded.
    /// </param>
    /// <param name="progress">
    /// Receives synchronous chapter-level progress notifications.
    /// </param>
    /// <returns>A summary of processed and skipped chapters.</returns>
    public async Task<CharacterDiscoveryWorkflowResult> ExecuteAsync(
        BookProject book,
        CancellationToken cancellationToken = default,
        bool force = false,
        IProgress<CharacterDiscoveryProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(book);

        var registry = await registryStore.LoadAsync(
            book,
            cancellationToken);

        var processedChapters = 0;
        var skippedChapters = 0;

        for (var chapterIndex = 0;
             chapterIndex < book.Manuscript.Chapters.Count;
             chapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceChapter =
                book.Manuscript.Chapters[chapterIndex];

            var preparedChapter = preparer.Prepare(
                sourceChapter);

            var stopwatch = Stopwatch.StartNew();

            if (!force && registry is not null &&
                registry.ProcessedChapterHashes.TryGetValue(
                    preparedChapter.ChapterId,
                    out var processedHash) &&
                string.Equals(
                    processedHash,
                    preparedChapter.SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                skippedChapters++;
                stopwatch.Stop();

                progress?.Report(
                    new CharacterDiscoveryProgress
                    {
                        ChapterIndex = chapterIndex,
                        ChapterCount =
                            book.Manuscript.Chapters.Count,
                        ChapterId = preparedChapter.ChapterId,
                        Status = "skipped",
                        DiscoveredCharacters = 0,
                        RegistryCharacters =
                            registry.Characters.Count,
                        Elapsed = stopwatch.Elapsed
                    });

                continue;
            }

            progress?.Report(
                new CharacterDiscoveryProgress
                {
                    ChapterIndex = chapterIndex,
                    ChapterCount =
                        book.Manuscript.Chapters.Count,
                    ChapterId = preparedChapter.ChapterId,
                    Status = "starting",
                    DiscoveredCharacters = 0,
                    RegistryCharacters =
                        registry?.Characters.Count ?? 0,
                    Elapsed = TimeSpan.Zero
                });

            var knownCharacters =
                registry?.Characters ?? [];

            var discoveries =
                await discoveryService.DiscoverAsync(
                    preparedChapter,
                    knownCharacters,
                    cancellationToken);

            registry = merger.Merge(
                registry,
                book.Id,
                preparedChapter.ChapterId,
                preparedChapter.SourceSha256,
                discoveries);

            await registryStore.SaveAsync(
                book,
                registry,
                cancellationToken);

            processedChapters++;
            stopwatch.Stop();

            progress?.Report(
                new CharacterDiscoveryProgress
                {
                    ChapterIndex = chapterIndex,
                    ChapterCount =
                        book.Manuscript.Chapters.Count,
                    ChapterId = preparedChapter.ChapterId,
                    Status = "completed",
                    DiscoveredCharacters = discoveries.Count,
                    RegistryCharacters =
                        registry.Characters.Count,
                    Elapsed = stopwatch.Elapsed
                });
        }

        return new CharacterDiscoveryWorkflowResult
        {
            ProcessedChapters = processedChapters,
            SkippedChapters = skippedChapters,
            CharacterCount = registry?.Characters.Count ?? 0
        };
    }
}
