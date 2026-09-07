using StoryCast.Application.Manuscripts;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Infrastructure.Manuscripts;

/// <summary>
/// Loads Markdown and plain-text manuscripts from the filesystem.
/// </summary>
public sealed class FileSystemManuscriptLoader : IManuscriptLoader
{
    private static readonly IReadOnlyDictionary<
        string,
        ManuscriptFormat> SupportedExtensions =
        new Dictionary<string, ManuscriptFormat>(
            StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = ManuscriptFormat.Markdown,
            [".markdown"] = ManuscriptFormat.Markdown,
            [".txt"] = ManuscriptFormat.PlainText
        };

    /// <inheritdoc />
    public async Task<Manuscript> LoadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        IReadOnlyList<string> sourceFiles;

        if (File.Exists(fullSourcePath))
        {
            EnsureSupportedFile(fullSourcePath);
            sourceFiles = [fullSourcePath];
        }
        else if (Directory.Exists(fullSourcePath))
        {
            sourceFiles = Directory
                .EnumerateFiles(
                    fullSourcePath,
                    "*",
                    SearchOption.AllDirectories)
                .Where(IsSupportedFile)
                .OrderBy(
                    path => Path.GetRelativePath(
                        fullSourcePath,
                        path),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (sourceFiles.Count == 0)
            {
                throw new InvalidDataException(
                    $"No supported manuscript files were found beneath: " +
                    fullSourcePath);
            }
        }
        else
        {
            throw new FileNotFoundException(
                $"Manuscript source was not found: {fullSourcePath}",
                fullSourcePath);
        }

        var chapters = new List<ManuscriptChapter>(
            sourceFiles.Count);

        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = sourceFiles[index];
            var extension = Path.GetExtension(file);

            var rawText = await File.ReadAllTextAsync(
                file,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                throw new InvalidDataException(
                    $"Manuscript chapter is empty: {file}");
            }

            chapters.Add(
                new ManuscriptChapter
                {
                    Id = $"chapter-{index + 1:000}",
                    Index = index,
                    FileName = Path.GetFileName(file),
                    SourcePath = Path.GetFullPath(file),
                    Format = SupportedExtensions[extension],
                    RawText = rawText
                });
        }

        return new Manuscript
        {
            SourcePath = fullSourcePath,
            Chapters = chapters
        };
    }

    private static bool IsSupportedFile(string path)
    {
        return SupportedExtensions.ContainsKey(
            Path.GetExtension(path));
    }

    private static void EnsureSupportedFile(string path)
    {
        if (!IsSupportedFile(path))
        {
            throw new InvalidDataException(
                $"Unsupported manuscript file type: {path}");
        }
    }
}
