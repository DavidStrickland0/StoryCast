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
    /// <returns>A summary of processed and skipped chapters.</returns>
    public async Task<CharacterDiscoveryWorkflowResult> ExecuteAsync(
        BookProject book,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(book);

        var registry = await registryStore.LoadAsync(
            book,
            cancellationToken);

        var processedChapters = 0;
        var skippedChapters = 0;

        foreach (var sourceChapter in book.Manuscript.Chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preparedChapter = preparer.Prepare(
                sourceChapter);

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
                continue;
            }

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
        }

        return new CharacterDiscoveryWorkflowResult
        {
            ProcessedChapters = processedChapters,
            SkippedChapters = skippedChapters,
            CharacterCount = registry?.Characters.Count ?? 0
        };
    }
}
