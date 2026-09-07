using StoryCast.Application.Attribution;
using StoryCast.Application.Characters;
using StoryCast.Application.Preparation;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Tests.Characters;

/// <summary>
/// Tests resumable book-level character discovery.
/// </summary>
public sealed class CharacterDiscoveryWorkflowTests
{
    /// <summary>
    /// Verifies that every changed chapter is discovered and saved.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ProcessesAndPersistsChapters()
    {
        var store = new MemoryCharacterRegistryStore();
        var discovery = new StubCharacterDiscoveryService();

        var workflow = new CharacterDiscoveryWorkflow(
            new ChapterTextPreparer(),
            discovery,
            new CharacterRegistryMerger(),
            store);

        var result = await workflow.ExecuteAsync(
            CreateBook());

        Assert.Equal(2, result.ProcessedChapters);
        Assert.Equal(0, result.SkippedChapters);
        Assert.Equal(2, result.CharacterCount);
        Assert.Equal(2, discovery.CallCount);
        Assert.Equal(2, store.SaveCount);

        Assert.NotNull(store.Registry);
        Assert.Equal(
            2,
            store.Registry.ProcessedChapterHashes.Count);
    }

    /// <summary>
    /// Verifies that chapters whose source hashes are unchanged are skipped.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SkipsUnchangedChapters()
    {
        var book = CreateBook();
        var preparer = new ChapterTextPreparer();

        var firstPrepared = preparer.Prepare(
            book.Manuscript.Chapters[0]);

        var store = new MemoryCharacterRegistryStore
        {
            Registry = new CharacterRegistry
            {
                SchemaVersion = 1,
                BookId = book.Id,
                Characters =
                [
                    CreateCharacter(
                        "marcia-miller",
                        "Marcia Miller")
                ],
                ProcessedChapterHashes =
                    new Dictionary<string, string>
                    {
                        [firstPrepared.ChapterId] =
                            firstPrepared.SourceSha256
                    }
            }
        };

        var discovery = new StubCharacterDiscoveryService();

        var workflow = new CharacterDiscoveryWorkflow(
            preparer,
            discovery,
            new CharacterRegistryMerger(),
            store);

        var result = await workflow.ExecuteAsync(book);

        Assert.Equal(1, result.ProcessedChapters);
        Assert.Equal(1, result.SkippedChapters);
        Assert.Equal(1, discovery.CallCount);
        Assert.Equal(1, store.SaveCount);
    }

    private static BookProject CreateBook()
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
                        SourcePath = @"C:\Book\chapter-001.md",
                        Format = ManuscriptFormat.Markdown,
                        RawText =
                            "Miller said, \"Move.\""
                    },
                    new ManuscriptChapter
                    {
                        Id = "chapter-002",
                        Index = 1,
                        FileName = "chapter-002.md",
                        SourcePath = @"C:\Book\chapter-002.md",
                        Format = ManuscriptFormat.Markdown,
                        RawText =
                            "Thorne answered, \"Moving.\""
                    }
                ]
            }
        };
    }

    private static CharacterProfile CreateCharacter(
        string id,
        string displayName)
    {
        return new CharacterProfile
        {
            Id = id,
            DisplayName = displayName,
            Aliases = [],
            Description = "",
            VoiceTraits = [],
            Importance = CharacterImportance.Major,
            IsNarrator = false
        };
    }

    private sealed class StubCharacterDiscoveryService
        : ICharacterDiscoveryService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<CharacterProfile>> DiscoverAsync(
            PreparedChapter chapter,
            IReadOnlyList<CharacterProfile> knownCharacters,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            IReadOnlyList<CharacterProfile> characters =
                chapter.ChapterId == "chapter-001"
                    ?
                    [
                        CreateCharacter(
                            "marcia-miller",
                            "Marcia Miller")
                    ]
                    :
                    [
                        CreateCharacter(
                            "elias-thorne",
                            "Elias Thorne")
                    ];

            return Task.FromResult(characters);
        }
    }

    private sealed class MemoryCharacterRegistryStore
        : ICharacterRegistryStore
    {
        public CharacterRegistry? Registry { get; set; }

        public int SaveCount { get; private set; }

        public Task<CharacterRegistry?> LoadAsync(
            BookProject book,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Registry);
        }

        public Task SaveAsync(
            BookProject book,
            CharacterRegistry registry,
            CancellationToken cancellationToken = default)
        {
            Registry = registry;
            SaveCount++;

            return Task.CompletedTask;
        }
    }
}
