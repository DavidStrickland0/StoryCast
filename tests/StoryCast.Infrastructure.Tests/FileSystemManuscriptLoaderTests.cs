using StoryCast.Domain.Manuscripts;
using StoryCast.Infrastructure.Manuscripts;

namespace StoryCast.Infrastructure.Tests.Manuscripts;

/// <summary>
/// Tests filesystem manuscript discovery and loading.
/// </summary>
public sealed class FileSystemManuscriptLoaderTests
{
    /// <summary>
    /// Verifies deterministic ordering and exact preservation of chapter text.
    /// </summary>
    [Fact]
    public async Task LoadAsync_LoadsSupportedChaptersInOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-manuscript-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            await File.WriteAllTextAsync(
                Path.Combine(root, "chapter-002.txt"),
                "Second chapter.\r\nUnchanged.");

            await File.WriteAllTextAsync(
                Path.Combine(root, "chapter-001.md"),
                "# First Chapter\n\nOriginal text.");

            await File.WriteAllTextAsync(
                Path.Combine(root, "cover.jpg"),
                "Not a manuscript chapter.");

            var loader = new FileSystemManuscriptLoader();

            var manuscript = await loader.LoadAsync(root);

            Assert.Equal(2, manuscript.Chapters.Count);

            var first = manuscript.Chapters[0];
            var second = manuscript.Chapters[1];

            Assert.Equal("chapter-001", first.Id);
            Assert.Equal("chapter-001.md", first.FileName);
            Assert.Equal(ManuscriptFormat.Markdown, first.Format);
            Assert.Equal(
                "# First Chapter\n\nOriginal text.",
                first.RawText);

            Assert.Equal("chapter-002", second.Id);
            Assert.Equal("chapter-002.txt", second.FileName);
            Assert.Equal(ManuscriptFormat.PlainText, second.Format);
            Assert.Equal(
                "Second chapter.\r\nUnchanged.",
                second.RawText);
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
    /// Verifies that an empty manuscript directory is rejected.
    /// </summary>
    [Fact]
    public async Task LoadAsync_RejectsDirectoryWithoutSupportedFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-manuscript-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            var loader = new FileSystemManuscriptLoader();

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
}
