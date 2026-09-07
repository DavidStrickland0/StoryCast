using StoryCast.Domain.Books;
using StoryCast.Domain.Manuscripts;
using StoryCast.Infrastructure.Production;

namespace StoryCast.Infrastructure.Tests.Production;

/// <summary>
/// Tests creation of isolated production-run directories.
/// </summary>
public sealed class FileSystemProductionRunFactoryTests
{
    /// <summary>
    /// Verifies that separate runs receive separate output directories.
    /// </summary>
    [Fact]
    public async Task CreateAsync_CreatesUniqueOutputForEveryRun()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-runs-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            var book = CreateBook(root);
            var factory = new FileSystemProductionRunFactory();

            var first = await factory.CreateAsync(book);
            var second = await factory.CreateAsync(book);

            Assert.NotEqual(first.Id, second.Id);
            Assert.NotEqual(first.OutputPath, second.OutputPath);

            Assert.True(Directory.Exists(first.OutputPath));
            Assert.True(Directory.Exists(second.OutputPath));

            Assert.True(
                File.Exists(
                    Path.Combine(first.OutputPath, "run.json")));

            Assert.True(
                File.Exists(
                    Path.Combine(second.OutputPath, "run.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that an explicit existing output path is never overwritten.
    /// </summary>
    [Fact]
    public async Task CreateAsync_RejectsExistingExplicitOutput()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-runs-{Guid.NewGuid():N}");

        var output = Path.Combine(root, "existing-output");

        try
        {
            Directory.CreateDirectory(output);

            var book = CreateBook(root);
            var factory = new FileSystemProductionRunFactory();

            await Assert.ThrowsAsync<IOException>(
                () => factory.CreateAsync(book, output));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
