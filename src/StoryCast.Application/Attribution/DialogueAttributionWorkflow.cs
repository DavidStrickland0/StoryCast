using StoryCast.Application.Characters;
using StoryCast.Application.Preparation;
using StoryCast.Application.Production;
using StoryCast.Domain.Books;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Creates and persists fully attributed production scripts for a book.
/// </summary>
public sealed class DialogueAttributionWorkflow
{
    private const decimal LowConfidenceThreshold = 0.60m;

    private readonly IChapterTextPreparer textPreparer;
    private readonly IProductionScriptSegmenter segmenter;
    private readonly IDialogueAttributionService attributionService;
    private readonly DialogueAttributionApplicator attributionApplicator;
    private readonly ICharacterRegistryStore characterRegistryStore;
    private readonly IProductionScriptStore productionScriptStore;

    /// <summary>
    /// Initializes the dialogue-attribution workflow.
    /// </summary>
    /// <param name="textPreparer">
    /// The deterministic chapter-text preparer.
    /// </param>
    /// <param name="segmenter">
    /// The deterministic production-script segmenter.
    /// </param>
    /// <param name="attributionService">
    /// The service that identifies dialogue speakers.
    /// </param>
    /// <param name="attributionApplicator">
    /// The validator that applies generated assignments.
    /// </param>
    /// <param name="characterRegistryStore">
    /// The persistent character-registry store.
    /// </param>
    /// <param name="productionScriptStore">
    /// The persistent production-script store.
    /// </param>
    public DialogueAttributionWorkflow(
        IChapterTextPreparer textPreparer,
        IProductionScriptSegmenter segmenter,
        IDialogueAttributionService attributionService,
        DialogueAttributionApplicator attributionApplicator,
        ICharacterRegistryStore characterRegistryStore,
        IProductionScriptStore productionScriptStore)
    {
        this.textPreparer = textPreparer ??
            throw new ArgumentNullException(nameof(textPreparer));

        this.segmenter = segmenter ??
            throw new ArgumentNullException(nameof(segmenter));

        this.attributionService = attributionService ??
            throw new ArgumentNullException(nameof(attributionService));

        this.attributionApplicator = attributionApplicator ??
            throw new ArgumentNullException(nameof(attributionApplicator));

        this.characterRegistryStore = characterRegistryStore ??
            throw new ArgumentNullException(nameof(characterRegistryStore));

        this.productionScriptStore = productionScriptStore ??
            throw new ArgumentNullException(nameof(productionScriptStore));
    }

    /// <summary>
    /// Attributes and persists every changed chapter in a book.
    /// </summary>
    /// <param name="book">The configured book project.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <param name="force">
    /// Whether existing current artifacts should be regenerated.
    /// </param>
    /// <returns>A summary of the completed attribution work.</returns>
    public async Task<DialogueAttributionWorkflowResult> ExecuteAsync(
        BookProject book,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(book);

        var registry = await characterRegistryStore.LoadAsync(
            book,
            cancellationToken);

        if (registry is null)
        {
            throw new InvalidDataException(
                "Character discovery must be completed before dialogue " +
                $"attribution for book '{book.Id}'.");
        }

        var processedChapters = 0;
        var skippedChapters = 0;
        var dialogueSegments = 0;
        var lowConfidenceAssignments = 0;

        foreach (var chapter in book.Manuscript.Chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preparedChapter = textPreparer.Prepare(chapter);

            if (!registry.ProcessedChapterHashes.TryGetValue(
                    preparedChapter.ChapterId,
                    out var discoveredHash) ||
                !string.Equals(
                    discoveredHash,
                    preparedChapter.SourceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Character discovery is missing or stale for chapter " +
                    $"'{preparedChapter.ChapterId}'. Run characters " +
                    "discover again.");
            }

            var existingArtifact =
                await productionScriptStore.LoadAsync(
                    book,
                    preparedChapter.ChapterId,
                    cancellationToken);

            if (!force &&
                existingArtifact is not null &&
                string.Equals(
                    existingArtifact.SourceSha256,
                    preparedChapter.SourceSha256,
                    StringComparison.Ordinal) &&
                string.Equals(
                    existingArtifact.PreparationVersion,
                    preparedChapter.PreparationVersion,
                    StringComparison.Ordinal))
            {
                CountDialogue(
                    existingArtifact.Script,
                    ref dialogueSegments,
                    ref lowConfidenceAssignments);

                skippedChapters++;
                continue;
            }

            var unattributedScript = segmenter.Segment(
                preparedChapter);

            var assignments =
                await attributionService.AttributeAsync(
                    unattributedScript,
                    registry,
                    cancellationToken);

            var attributedScript = attributionApplicator.Apply(
                unattributedScript,
                registry,
                assignments);

            var artifact = new ChapterProductionArtifact
            {
                SchemaVersion = 1,
                BookId = book.Id,
                SourceSha256 = preparedChapter.SourceSha256,
                PreparationVersion =
                    preparedChapter.PreparationVersion,
                Script = attributedScript
            };

            await productionScriptStore.SaveAsync(
                book,
                artifact,
                cancellationToken);

            CountDialogue(
                attributedScript,
                ref dialogueSegments,
                ref lowConfidenceAssignments);

            processedChapters++;
        }

        return new DialogueAttributionWorkflowResult
        {
            ProcessedChapters = processedChapters,
            SkippedChapters = skippedChapters,
            DialogueSegments = dialogueSegments,
            LowConfidenceAssignments =
                lowConfidenceAssignments
        };
    }

    private static void CountDialogue(
        ChapterProductionScript script,
        ref int dialogueSegments,
        ref int lowConfidenceAssignments)
    {
        foreach (var segment in script.Segments.Where(
                     segment =>
                         segment.Kind == SegmentKind.Dialogue))
        {
            dialogueSegments++;

            if (segment.AttributionConfidence <
                LowConfidenceThreshold)
            {
                lowConfidenceAssignments++;
            }
        }
    }
}
