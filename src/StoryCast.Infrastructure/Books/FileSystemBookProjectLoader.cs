using System.Text.Json;
using StoryCast.Application.Books;
using StoryCast.Domain.Books;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Infrastructure.Books;

/// <summary>
/// Loads audiobook projects from book.json manifests.
/// </summary>
public sealed class FileSystemBookProjectLoader : IBookProjectLoader
{
    private const string ManifestFileName = "book.json";
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    /// <inheritdoc />
    public async Task<BookProject> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var resolvedPath = Path.GetFullPath(projectPath);

        var manifestPath = Directory.Exists(resolvedPath)
            ? Path.Combine(resolvedPath, ManifestFileName)
            : resolvedPath;

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Book manifest was not found: {manifestPath}",
                manifestPath);
        }

        var rootPath =
            Path.GetDirectoryName(manifestPath) ??
            throw new InvalidDataException(
                $"Book manifest has no parent directory: {manifestPath}");

        await using var stream = File.OpenRead(manifestPath);

        var manifest =
            await JsonSerializer.DeserializeAsync<BookManifest>(
                stream,
                SerializerOptions,
                cancellationToken) ??
            throw new InvalidDataException(
                $"Book manifest could not be read: {manifestPath}");

        ValidateManifest(manifest, manifestPath);

        var configuredChapters = manifest.Chapters.ToList();
        var chapterDirectory = Path.Combine(
            rootPath,
            "chapters");

        if (Directory.Exists(chapterDirectory))
        {
            var discoveredChapters = Directory
                .EnumerateFiles(
                    chapterDirectory,
                    "chapter-*",
                    SearchOption.TopDirectoryOnly)
                .Where(
                    path =>
                        Path.GetExtension(path).Equals(
                            ".md",
                            StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path).Equals(
                            ".markdown",
                            StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path).Equals(
                            ".txt",
                            StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    path => Path.GetFileName(path),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var discoveredChapter in discoveredChapters)
            {
                var relativePath = Path.GetRelativePath(
                    rootPath,
                    discoveredChapter);

                var alreadyConfigured =
                    configuredChapters.Any(
                        configuredPath =>
                            string.Equals(
                                Path.GetFullPath(
                                    Path.Combine(
                                        rootPath,
                                        configuredPath)),
                                Path.GetFullPath(
                                    discoveredChapter),
                                StringComparison.OrdinalIgnoreCase));

                if (!alreadyConfigured)
                {
                    configuredChapters.Add(relativePath);
                }
            }
        }


        if (configuredChapters.Count == 0)
        {
            throw new InvalidDataException(
                $"No chapters were found for book: {manifestPath}");
        }

        var chapterPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var chapters = new List<ManuscriptChapter>(
            configuredChapters.Count);

        for (var index = 0;
             index < configuredChapters.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var configuredPath = configuredChapters[index];

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new InvalidDataException(
                    $"Chapter entry {index} is empty in {manifestPath}.");
            }

            var chapterPath = ResolveContainedPath(
                rootPath,
                configuredPath,
                manifestPath);

            if (!chapterPaths.Add(chapterPath))
            {
                throw new InvalidDataException(
                    $"Chapter is listed more than once: {configuredPath}");
            }

            if (!File.Exists(chapterPath))
            {
                throw new FileNotFoundException(
                    $"Configured chapter was not found: {chapterPath}",
                    chapterPath);
            }

            var format = GetFormat(chapterPath);

            var rawText = await File.ReadAllTextAsync(
                chapterPath,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                throw new InvalidDataException(
                    $"Configured chapter is empty: {chapterPath}");
            }

            chapters.Add(
                new ManuscriptChapter
                {
                    Id = $"chapter-{index + 1:000}",
                    Index = index,
                    FileName = Path.GetFileName(chapterPath),
                    SourcePath = chapterPath,
                    Format = format,
                    RawText = rawText
                });
        }

        return new BookProject
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            Title = manifest.Title,
            Author = manifest.Author,
            Language = manifest.Language,
            RootPath = rootPath,
            Manuscript = new Manuscript
            {
                SourcePath = manifestPath,
                Chapters = chapters
            }
        };
    }

    private static void ValidateManifest(
        BookManifest manifest,
        string manifestPath)
    {
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Book manifest schema version " +
                $"{manifest.SchemaVersion} is unsupported. " +
                $"Expected {SupportedSchemaVersion}: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException(
                $"Book manifest is missing an ID: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Title))
        {
            throw new InvalidDataException(
                $"Book manifest is missing a title: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Author))
        {
            throw new InvalidDataException(
                $"Book manifest is missing an author: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Language))
        {
            throw new InvalidDataException(
                $"Book manifest is missing a language: {manifestPath}");
        }

    }

    private static string ResolveContainedPath(
        string rootPath,
        string configuredPath,
        string manifestPath)
    {
        var resolvedPath = Path.GetFullPath(
            Path.Combine(rootPath, configuredPath));

        var relativePath = Path.GetRelativePath(
            rootPath,
            resolvedPath);

        if (relativePath == ".." ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Configured chapter must remain inside the book project: " +
                $"{manifestPath}");
        }

        return resolvedPath;
    }

    private static ManuscriptFormat GetFormat(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" or ".markdown" => ManuscriptFormat.Markdown,
            ".txt" => ManuscriptFormat.PlainText,
            _ => throw new InvalidDataException(
                $"Unsupported configured chapter type: {path}")
        };
    }

    private sealed class BookManifest
    {
        public int SchemaVersion { get; init; }

        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public string Language { get; init; } = string.Empty;

        public IReadOnlyList<string> Chapters { get; init; } = [];
    }
}
