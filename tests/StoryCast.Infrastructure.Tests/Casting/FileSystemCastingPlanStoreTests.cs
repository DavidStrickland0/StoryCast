using StoryCast.Domain.Books;
using StoryCast.Domain.Voices;
using StoryCast.Infrastructure.Casting;

namespace StoryCast.Infrastructure.Tests.Casting;

/// <summary>
/// Tests filesystem persistence of per-book casting plans.
/// </summary>
public sealed class FileSystemCastingPlanStoreTests
{
    /// <summary>
    /// Verifies that assignments and manual locks survive persistence.
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAsync_LockedAssignment_RoundTrips()
    {
        var rootPath = CreateTemporaryDirectory();

        try
        {
            var book = CreateBook(
                rootPath,
                "book-1");

            var store =
                new FileSystemCastingPlanStore();

            var plan = new CastingPlan
            {
                SchemaVersion = 1,
                BookId = book.Id,
                Assignments =
                [
                    new CastingAssignment
                    {
                        CharacterId = "narrator",
                        VoiceId = "voice-1",
                        Confidence = 0.95m,
                        Rationale = "Clear sustained narration.",
                        IsLocked = true
                    }
                ]
            };

            await store.SaveAsync(
                book,
                plan);

            var loaded = await store.LoadAsync(book);

            Assert.NotNull(loaded);

            var assignment =
                Assert.Single(loaded.Assignments);

            Assert.Equal(
                "narrator",
                assignment.CharacterId);

            Assert.Equal(
                "voice-1",
                assignment.VoiceId);

            Assert.Equal(
                0.95m,
                assignment.Confidence);

            Assert.True(assignment.IsLocked);
        }
        finally
        {
            Directory.Delete(
                rootPath,
                recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a plan cannot be saved under a different book.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WrongBook_ThrowsInvalidDataException()
    {
        var rootPath = CreateTemporaryDirectory();

        try
        {
            var book = CreateBook(
                rootPath,
                "book-1");

            var store =
                new FileSystemCastingPlanStore();

            var plan = new CastingPlan
            {
                SchemaVersion = 1,
                BookId = "book-2",
                Assignments = []
            };

            var exception =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => store.SaveAsync(
                        book,
                        plan));

            Assert.Contains(
                "book-2",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(
                rootPath,
                recursive: true);
        }
    }

    /// <summary>
    /// Verifies that one voice cannot be persisted for multiple roles.
    /// </summary>
    [Fact]
    public async Task SaveAsync_DuplicateVoice_ThrowsInvalidDataException()
    {
        var rootPath = CreateTemporaryDirectory();

        try
        {
            var book = CreateBook(
                rootPath,
                "book-1");

            var store =
                new FileSystemCastingPlanStore();

            var plan = new CastingPlan
            {
                SchemaVersion = 1,
                BookId = book.Id,
                Assignments =
                [
                    new CastingAssignment
                    {
                        CharacterId = "character-1",
                        VoiceId = "voice-1",
                        Confidence = 0.8m
                    },
                    new CastingAssignment
                    {
                        CharacterId = "narrator",
                        VoiceId = "voice-1",
                        Confidence = 0.9m
                    }
                ]
            };

            var exception =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => store.SaveAsync(
                        book,
                        plan));

            Assert.Contains(
                "more than once",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(
                rootPath,
                recursive: true);
        }
    }

    private static BookProject CreateBook(
        string rootPath,
        string bookId)
    {
        return new BookProject
        {
            SchemaVersion = 1,
            Id = bookId,
            Title = "Test Book",
            Author = "Test Author",
            Language = "en",
            RootPath = rootPath,
            Manuscript = null!
        };
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"storycast-casting-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        return path;
    }
}
