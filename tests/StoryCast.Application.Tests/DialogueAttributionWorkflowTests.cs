using StoryCast.Application.Attribution;
using StoryCast.Application.Characters;
using StoryCast.Application.Preparation;
using StoryCast.Application.Production;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests chapter-scoped dialogue attribution.
/// </summary>
public sealed class DialogueAttributionWorkflowTests
{
    /// <summary>
    /// Verifies that a changed chapter source replaces its previously stored script.
    /// </summary>
    /// <returns>A task representing the asynchronous test operation.</returns>
    [Fact]
    public async Task ExecuteAsync_ChangedSource_ReplacesScript()
    {
        var preparer = new ChapterTextPreparer();
        var scriptStore = new MemoryProductionScriptStore();
        var originalBook = CreateBook();
        var originalWorkflow = new DialogueAttributionWorkflow(
            preparer,
            new ProductionScriptSegmenter(),
            new StubDialogueAttributionService(),
            new DialogueAttributionApplicator(),
            new MemoryCharacterRegistryStore(
                CreateRegistry(originalBook, preparer)),
            scriptStore);

        await originalWorkflow.ExecuteAsync(
            originalBook,
            chapterId: "chapter-002");
        var originalHash = scriptStore.Artifact!.SourceSha256;

        var revisedBook = CreateBook(
            "Thorne answered, \"Moving with the child.\"");
        var revisedWorkflow = new DialogueAttributionWorkflow(
            preparer,
            new ProductionScriptSegmenter(),
            new StubDialogueAttributionService(),
            new DialogueAttributionApplicator(),
            new MemoryCharacterRegistryStore(
                CreateRegistry(revisedBook, preparer)),
            scriptStore);

        var result = await revisedWorkflow.ExecuteAsync(
            revisedBook,
            chapterId: "chapter-002");

        Assert.Equal(1, result.ProcessedChapters);
        Assert.Equal(2, scriptStore.SaveCount);
        Assert.NotEqual(originalHash, scriptStore.Artifact!.SourceSha256);
        Assert.Contains(
            "Moving with the child.",
            string.Concat(
                scriptStore.Artifact.Script.Segments.Select(
                    segment => segment.SourceText)));
    }

    /// <summary>
    /// Verifies that only the requested chapter is attributed and persisted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ProcessesOnlyRequestedChapter()
    {
        var book = CreateBook();
        var preparer = new ChapterTextPreparer();
        var registry = CreateRegistry(
            book,
            preparer);

        var attributionService =
            new StubDialogueAttributionService();

        var scriptStore =
            new MemoryProductionScriptStore();

        var workflow = new DialogueAttributionWorkflow(
            preparer,
            new ProductionScriptSegmenter(),
            attributionService,
            new DialogueAttributionApplicator(),
            new MemoryCharacterRegistryStore(registry),
            scriptStore);

        var result = await workflow.ExecuteAsync(
            book,
            chapterId: "chapter-002");

        Assert.Equal(1, result.ProcessedChapters);
        Assert.Equal(0, result.SkippedChapters);
        Assert.Equal(1, result.DialogueSegments);
        Assert.Equal(0, result.LowConfidenceAssignments);

        Assert.Equal(1, attributionService.CallCount);
        Assert.Equal(
            "chapter-002",
            attributionService.ChapterId);

        Assert.Equal(1, scriptStore.LoadCount);
        Assert.Equal(
            "chapter-002",
            scriptStore.LoadedChapterId);

        Assert.Equal(1, scriptStore.SaveCount);
        Assert.NotNull(scriptStore.Artifact);
        Assert.Equal(
            "chapter-002",
            scriptStore.Artifact.Script.ChapterId);

        var dialogue = Assert.Single(
            scriptStore.Artifact.Script.Segments,
            segment => segment.Kind == SegmentKind.Dialogue);

        Assert.Equal(
            "elias-thorne",
            dialogue.SpeakerId);
    }

    private static BookProject CreateBook(
        string chapterTwoText = "Thorne answered, \"Moving.\"")
    {
        return new BookProject
        {
            SchemaVersion = 1,
            Id = "test-book",
            Title = "Test Book",
            Author = "Test Author",
            Language = "en",
            RootPath = @"C:\Book",
            Manuscript = new Manuscript
            {
                SourcePath = @"C:\Book\book.json",
                Chapters =
                [
                    new ManuscriptChapter
                    {
                        Id = "chapter-001",
                        Index = 0,
                        FileName = "chapter-001.md",
                        SourcePath =
                            @"C:\Book\chapter-001.md",
                        Format = ManuscriptFormat.Markdown,
                        RawText =
                            "Miller said, \"Move.\""
                    },
                    new ManuscriptChapter
                    {
                        Id = "chapter-002",
                        Index = 1,
                        FileName = "chapter-002.md",
                        SourcePath =
                            @"C:\Book\chapter-002.md",
                        Format = ManuscriptFormat.Markdown,
                        RawText =
                            chapterTwoText
                    }
                ]
            }
        };
    }

    private static CharacterRegistry CreateRegistry(
        BookProject book,
        IChapterTextPreparer preparer)
    {
        var hashes = book.Manuscript.Chapters.ToDictionary(
            chapter => chapter.Id,
            chapter => preparer.Prepare(chapter).SourceSha256,
            StringComparer.OrdinalIgnoreCase);

        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = book.Id,
            Characters =
            [
                new CharacterProfile
                {
                    Id = "marcia-miller",
                    DisplayName = "Marcia Miller"
                },
                new CharacterProfile
                {
                    Id = "elias-thorne",
                    DisplayName = "Elias Thorne"
                }
            ],
            ProcessedChapterHashes = hashes
        };
    }

    private sealed class StubDialogueAttributionService
        : IDialogueAttributionService
    {
        public int CallCount { get; private set; }

        public string ChapterId { get; private set; } =
            string.Empty;

        public Task<IReadOnlyList<DialogueAssignment>>
            AttributeAsync(
                ChapterProductionScript script,
                CharacterRegistry registry,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            ChapterId = script.ChapterId;

            IReadOnlyList<DialogueAssignment> assignments =
                script.Segments
                    .Where(
                        segment =>
                            segment.Kind == SegmentKind.Dialogue)
                    .Select(
                        segment => new DialogueAssignment
                        {
                            SegmentIndex = segment.Index,
                            SpeakerId = "elias-thorne",
                            Confidence = 0.95m,
                            Delivery = "Direct",
                            Rationale = "Thorne is named."
                        })
                    .ToArray();

            return Task.FromResult(assignments);
        }
    }

    private sealed class MemoryCharacterRegistryStore
        : ICharacterRegistryStore
    {
        private readonly CharacterRegistry registry;

        public MemoryCharacterRegistryStore(
            CharacterRegistry registry)
        {
            this.registry = registry;
        }

        public Task<CharacterRegistry?> LoadAsync(
            BookProject book,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CharacterRegistry?>(
                registry);
        }

        public Task SaveAsync(
            BookProject book,
            CharacterRegistry registry,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MemoryProductionScriptStore
        : IProductionScriptStore
    {
        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public string LoadedChapterId { get; private set; } =
            string.Empty;

        public ChapterProductionArtifact? Artifact
        { get; private set; }

        public Task<ChapterProductionArtifact?> LoadAsync(
            BookProject book,
            string chapterId,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LoadedChapterId = chapterId;

            return Task.FromResult<ChapterProductionArtifact?>(
                Artifact);
        }

        public Task SaveAsync(
            BookProject book,
            ChapterProductionArtifact artifact,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Artifact = artifact;

            return Task.CompletedTask;
        }
    }
}
