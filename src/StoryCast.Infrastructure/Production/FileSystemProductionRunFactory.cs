using System.Text.Json;
using StoryCast.Application.Production;
using StoryCast.Domain.Books;
using StoryCast.Domain.Production;

namespace StoryCast.Infrastructure.Production;

/// <summary>
/// Creates production runs in isolated filesystem directories.
/// </summary>
public sealed class FileSystemProductionRunFactory
    : IProductionRunFactory
{
    private const int RunManifestSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true
        };

    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a production-run factory using the system clock.
    /// </summary>
    public FileSystemProductionRunFactory()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a production-run factory using a specified clock.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock used to timestamp production runs.
    /// </param>
    public FileSystemProductionRunFactory(
        TimeProvider timeProvider)
    {
        this.timeProvider =
            timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<ProductionRun> CreateAsync(
        BookProject book,
        string? requestedOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);

        cancellationToken.ThrowIfCancellationRequested();

        var startedAtUtc = timeProvider.GetUtcNow();

        var runId =
            $"{startedAtUtc:yyyyMMdd-HHmmss-fff}-" +
            $"{Guid.NewGuid():N}"[..8];

        string outputPath;

        if (string.IsNullOrWhiteSpace(requestedOutputPath))
        {
            var outputRoot = Path.Combine(
                book.RootPath,
                "output");

            outputPath = Path.Combine(
                outputRoot,
                runId);
        }
        else
        {
            outputPath = Path.GetFullPath(
                requestedOutputPath);
        }

        ReserveDirectory(outputPath);

        var run = new ProductionRun
        {
            Id = runId,
            BookId = book.Id,
            StartedAtUtc = startedAtUtc,
            OutputPath = outputPath
        };

        try
        {
            await WriteManifestAsync(
                book,
                run,
                cancellationToken);
        }
        catch
        {
            TryRemoveEmptyRunDirectory(outputPath);
            throw;
        }

        return run;
    }

    private static void ReserveDirectory(string outputPath)
    {
        if (Directory.Exists(outputPath) ||
            File.Exists(outputPath))
        {
            throw new IOException(
                $"Production output path already exists: {outputPath}");
        }

        Directory.CreateDirectory(outputPath);
    }

    private static async Task WriteManifestAsync(
        BookProject book,
        ProductionRun run,
        CancellationToken cancellationToken)
    {
        var manifest = new RunManifest
        {
            SchemaVersion = RunManifestSchemaVersion,
            RunId = run.Id,
            BookId = book.Id,
            Title = book.Title,
            Author = book.Author,
            StartedAtUtc = run.StartedAtUtc,
            OutputPath = run.OutputPath
        };

        var manifestPath = Path.Combine(
            run.OutputPath,
            "run.json");

        await using var stream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        await JsonSerializer.SerializeAsync(
            stream,
            manifest,
            SerializerOptions,
            cancellationToken);

        await stream.WriteAsync(
            "\n"u8.ToArray(),
            cancellationToken);
    }

    private static void TryRemoveEmptyRunDirectory(
        string outputPath)
    {
        try
        {
            if (Directory.Exists(outputPath) &&
                !Directory.EnumerateFileSystemEntries(
                    outputPath).Any())
            {
                Directory.Delete(outputPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RunManifest
    {
        public int SchemaVersion { get; init; }

        public string RunId { get; init; } = string.Empty;

        public string BookId { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public DateTimeOffset StartedAtUtc { get; init; }

        public string OutputPath { get; init; } = string.Empty;
    }
}
