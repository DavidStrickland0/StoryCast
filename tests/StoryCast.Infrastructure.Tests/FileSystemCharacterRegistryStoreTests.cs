using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;
using StoryCast.Infrastructure.Characters;

namespace StoryCast.Infrastructure.Tests.Characters;

/// <summary>
/// Tests persistent character-registry storage.
/// </summary>
public sealed class FileSystemCharacterRegistryStoreTests
{
    /// <summary>
    /// Verifies that loading an uninitialized book returns no registry.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ReturnsNullWhenRegistryDoesNotExist()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var store = new FileSystemCharacterRegistryStore();

            var registry = await store.LoadAsync(
                CreateBook(root));

            Assert.Null(registry);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    /// <summary>
    /// Verifies that a saved registry can be loaded without identity changes.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PersistsAndLoadsRegistry()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var book = CreateBook(root);
            var expected = CreateRegistry("marcia-miller");
            var store = new FileSystemCharacterRegistryStore();

            await store.SaveAsync(book, expected);

            var actual = await store.LoadAsync(book);

            Assert.NotNull(actual);
            Assert.Equal(expected.BookId, actual.BookId);

            var character = Assert.Single(actual.Characters);

            Assert.Equal("marcia-miller", character.Id);
            Assert.Equal("Marcia Miller", character.DisplayName);
            Assert.Equal(
                CharacterImportance.Major,
                character.Importance);

            Assert.True(
                File.Exists(
                    Path.Combine(
                        root,
                        "production",
                        "characters.json")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    /// <summary>
    /// Verifies that saving again atomically replaces the existing registry.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ReplacesExistingRegistry()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var book = CreateBook(root);
            var store = new FileSystemCharacterRegistryStore();

            await store.SaveAsync(
                book,
                CreateRegistry("character-one"));

            await store.SaveAsync(
                book,
                CreateRegistry("character-two"));

            var actual = await store.LoadAsync(book);

            var character = Assert.Single(actual!.Characters);

            Assert.Equal("character-two", character.Id);

            Assert.Empty(
                Directory.EnumerateFiles(
                    Path.Combine(root, "production"),
                    "*.tmp"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-registry-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BookProject CreateBook(string root)
    {
        return new BookProject
        {
            SchemaVersion = 1,
            Id = "test-book",
            Title = "Test Book",
            Author = "Test Author",
            Language = "en",
            RootPath = root,
            Manuscript = new Manuscript
            {
                SourcePath = Path.Combine(root, "book.json"),
                Chapters = []
            }
        };
    }

    private static CharacterRegistry CreateRegistry(
        string characterId)
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "test-book",
            Characters =
            [
                new CharacterProfile
                {
                    Id = characterId,
                    DisplayName = characterId == "marcia-miller"
                        ? "Marcia Miller"
                        : characterId,
                    Aliases = [],
                    Description = "",
                    VoiceTraits = ["controlled"],
                    Importance = CharacterImportance.Major,
                    IsNarrator = false
                }
            ],
            ProcessedChapterHashes =
                new Dictionary<string, string>
                {
                    ["chapter-001"] = new string('a', 64)
                }
        };
    }
}
