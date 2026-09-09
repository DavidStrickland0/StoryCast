using StoryCast.Infrastructure.Books;

namespace StoryCast.Infrastructure.Tests.Books;

/// <summary>
/// Tests loading explicitly configured book projects.
/// </summary>
public sealed class FileSystemBookProjectLoaderTests
{
    /// <summary>
    /// Verifies that chapter order follows the manifest rather than filenames.
    /// </summary>
    [Fact]
    public async Task LoadAsync_UsesExplicitManifestOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-book-{Guid.NewGuid():N}");

        var manuscriptDirectory =
            Path.Combine(root, "manuscript");

        try
        {
            Directory.CreateDirectory(manuscriptDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(manuscriptDirectory, "opening.md"),
                "# Opening\n\nFirst.");

            await File.WriteAllTextAsync(
                Path.Combine(manuscriptDirectory, "ending.md"),
                "# Ending\n\nSecond.");

            await File.WriteAllTextAsync(
                Path.Combine(root, "book.json"),
                """
                {
                  "schemaVersion": 1,
                  "id": "test-book",
                  "title": "Test Book",
                  "author": "Test Author",
                  "language": "en",
                  "chapters": [
                    "manuscript/ending.md",
                    "manuscript/opening.md"
                  ]
                }
                """);

            var loader = new FileSystemBookProjectLoader();

            var project = await loader.LoadAsync(root);

            Assert.Equal("test-book", project.Id);
            Assert.Equal("Test Book", project.Title);
            Assert.Equal(2, project.Manuscript.Chapters.Count);

            Assert.Equal(
                "ending.md",
                project.Manuscript.Chapters[0].FileName);

            Assert.Equal(
                "opening.md",
                project.Manuscript.Chapters[1].FileName);
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
    /// Verifies that chapter paths cannot escape the project directory.
    /// </summary>
    [Fact]
    public async Task LoadAsync_RejectsChapterOutsideProject()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-book-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            await File.WriteAllTextAsync(
                Path.Combine(root, "book.json"),
                """
                {
                  "schemaVersion": 1,
                  "id": "test-book",
                  "title": "Test Book",
                  "author": "Test Author",
                  "language": "en",
                  "chapters": [
                    "../outside.md"
                  ]
                }
                """);

            var loader = new FileSystemBookProjectLoader();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => loader.LoadAsync(root));
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
    /// Verifies that newly added chapter files are discovered without
    /// changing the book manifest.
    /// </summary>
    [Fact]
    public async Task LoadAsync_DiscoversChapterAddedAfterManifest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-book-{Guid.NewGuid():N}");

        var chapters = Path.Combine(
            root,
            "chapters");

        try
        {
            Directory.CreateDirectory(chapters);

            await File.WriteAllTextAsync(
                Path.Combine(
                    chapters,
                    "chapter-001.md"),
                "# Chapter One\n\nFirst.");

            await File.WriteAllTextAsync(
                Path.Combine(root, "book.json"),
                """
                {
                  "schemaVersion": 1,
                  "id": "test-book",
                  "title": "Test Book",
                  "author": "Test Author",
                  "language": "en",
                  "chapters": [
                    "chapters/chapter-001.md"
                  ]
                }
                """);

            var loader =
                new FileSystemBookProjectLoader();

            var initial =
                await loader.LoadAsync(root);

            Assert.Single(
                initial.Manuscript.Chapters);

            await File.WriteAllTextAsync(
                Path.Combine(
                    chapters,
                    "chapter-002.md"),
                "# Chapter Two\n\nSecond.");

            var updated =
                await loader.LoadAsync(root);

            Assert.Equal(
                2,
                updated.Manuscript.Chapters.Count);

            Assert.Equal(
                "chapter-002.md",
                updated.Manuscript.Chapters[1].FileName);

            Assert.Equal(
                "chapter-002",
                updated.Manuscript.Chapters[1].Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }}
