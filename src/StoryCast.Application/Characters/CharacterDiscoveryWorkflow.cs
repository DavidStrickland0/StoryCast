using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using StoryCast.Application.Attribution;
using StoryCast.Application.Preparation;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;

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
    /// <param name="chapterId">
    /// The optional chapter to process. When omitted, every chapter is
    /// considered.
    /// </param>
    /// <param name="dialogueConventions">Book-specific speaker label rules.</param>
    /// <returns>A summary of processed and skipped chapters.</returns>
    public async Task<CharacterDiscoveryWorkflowResult> ExecuteAsync(
        BookProject book,
        CancellationToken cancellationToken = default,
        bool force = false,
        IProgress<CharacterDiscoveryProgress>? progress = null,
        string? chapterId = null,
        IReadOnlyList<DialogueConvention>? dialogueConventions = null)
    {
        ArgumentNullException.ThrowIfNull(book);

        var registry = await registryStore.LoadAsync(
            book,
            cancellationToken);

        var processedChapters = 0;
        var skippedChapters = 0;

        var chapterIndexes = Enumerable.Range(
                0,
                book.Manuscript.Chapters.Count)
            .Where(
                index =>
                    chapterId is null ||
                    string.Equals(
                        book.Manuscript.Chapters[index].Id,
                        chapterId,
                        StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (chapterId is not null &&
            chapterIndexes.Length == 0)
        {
            throw new InvalidDataException(
                $"Chapter '{chapterId}' was not found in book '{book.Id}'.");
        }

        foreach (var chapterIndex in chapterIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceChapter =
                book.Manuscript.Chapters[chapterIndex];

            var preparedChapter = preparer.Prepare(
                sourceChapter);

            var stopwatch = Stopwatch.StartNew();

            // Explicit named message headers are source evidence, even when an
            // earlier model discovery missed them and cached this chapter.
            var labeledSpeakers = new List<CharacterProfile>();
            foreach (var rule in dialogueConventions ?? [])
            {
                if (!rule.SpeakerLabelsOverride) continue;
                var pattern = Regex.Escape(rule.Open.ToString()) +
                    @"\s*(?<name>(?:(?!" + Regex.Escape(rule.Open.ToString()) + "|" +
                    Regex.Escape(rule.Close.ToString()) + @")[^\r\n:]){1,150}?)\s*\([^()\r\n]+\)\s*:";
                foreach (Match match in Regex.Matches(preparedChapter.SpokenText, pattern))
                {
                    var name = match.Groups["name"].Value.Trim();
                    var fullName = match.Value[1..^1].Trim();
                    if (rule.SpeakerIdsByLabel.Keys.Any(label =>
                        label.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        label.Equals(fullName, StringComparison.OrdinalIgnoreCase))) continue;
                    if ((registry?.Characters ?? []).Concat(labeledSpeakers).Any(character =>
                        character.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        character.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        character.Aliases.Any(alias => alias.Equals(name, StringComparison.OrdinalIgnoreCase)))) continue;
                    var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..12].ToLowerInvariant();
                    labeledSpeakers.Add(new CharacterProfile
                    {
                        Id = $"message-speaker-{slug}-{hash}", DisplayName = name,
                        IsNamed = true, Description = "Named participant in a book-configured dialogue message."
                    });
                }
            }
            if (labeledSpeakers.Count > 0)
            {
                // Retain the old hash so adding explicit labels does not skip
                // model discovery for new or changed prose.
                var previousHashes = registry?.ProcessedChapterHashes ?? new Dictionary<string, string>();
                var merged = merger.Merge(registry, book.Id, preparedChapter.ChapterId,
                    preparedChapter.SourceSha256, labeledSpeakers);
                registry = new CharacterRegistry
                {
                    SchemaVersion = merged.SchemaVersion, BookId = merged.BookId,
                    Characters = merged.Characters, ProcessedChapterHashes = previousHashes
                };
                await registryStore.SaveAsync(book, registry, cancellationToken);
            }

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
