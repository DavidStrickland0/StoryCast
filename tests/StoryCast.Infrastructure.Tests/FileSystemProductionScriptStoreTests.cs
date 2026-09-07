using StoryCast.Domain.Books;
using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;
using StoryCast.Infrastructure.Production;

namespace StoryCast.Infrastructure.Tests.Production;

/// <summary>
/// Tests persistent production-script storage.
/// </summary>
public sealed class FileSystemProductionScriptStoreTests
{
    /// <summary>
    /// Verifies that a verified attributed script survives a round trip.
    /// </summary>
    [Fact]
    public async Task SaveAsync_PersistsAndLoadsVerifiedScript()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var book = CreateBook(root);
            var artifact = CreateArtifact();
            var store = new FileSystemProductionScriptStore();

            await store.SaveAsync(book, artifact);

            var loaded = await store.LoadAsync(
                book,
                "chapter-001");

            Assert.NotNull(loaded);
            Assert.Equal(
                artifact.SourceSha256,
                loaded.SourceSha256);

            Assert.Equal(
                "marcia-miller",
                loaded.Script.Segments[1].SpeakerId);

            Assert.Equal(
                artifact.Script.SourceText,
                string.Concat(
                    loaded.Script.Segments.Select(
                        segment => segment.SourceText)));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    /// <summary>
    /// Verifies that unassigned dialogue cannot be persisted.
    /// </summary>
    [Fact]
    public async Task SaveAsync_RejectsUnassignedDialogue()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var book = CreateBook(root);

            var artifact = CreateArtifact(
                speakerId: "unassigned");

            var store = new FileSystemProductionScriptStore();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.SaveAsync(book, artifact));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static ChapterProductionArtifact CreateArtifact(
        string speakerId = "marcia-miller")
    {
        const string text =
            "Miller said, “Move.”";

        return new ChapterProductionArtifact
        {
            SchemaVersion = 1,
            BookId = "test-book",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            Script = new ChapterProductionScript
            {
                ChapterId = "chapter-001",
                SourceText = text,
                Segments =
                [
                    new ProductionSegment
                    {
                        Index = 0,
                        SourceStart = 0,
                        SourceLength = 13,
                        SourceText = "Miller said, ",
                        SpeakerId = "narrator",
                        Kind = SegmentKind.Narration
                    },
                    new ProductionSegment
                    {
                        Index = 1,
                        SourceStart = 13,
                        SourceLength = 7,
                        SourceText = "“Move.”",
                        SpeakerId = speakerId,
                        Kind = SegmentKind.Dialogue,
                        AttributionConfidence = 0.98m,
                        AttributionRationale =
                            "Miller is identified by the dialogue tag."
                    }
                ]
            }
        };
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-scripts-{Guid.NewGuid():N}");

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
}
