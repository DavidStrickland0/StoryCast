using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StoryCast.Application.Attribution;
using StoryCast.Application.Production;
using StoryCast.Domain.Books;
using StoryCast.Domain.Production;

namespace StoryCast.Infrastructure.Production;

/// <summary>
/// Stores verified production scripts as atomic JSON files.
/// </summary>
public sealed partial class FileSystemProductionScriptStore
    : IProductionScriptStore
{
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase)
            }
        };

    /// <inheritdoc />
    public async Task<ChapterProductionArtifact?> LoadAsync(
        BookProject book,
        string chapterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ValidateChapterId(chapterId);

        var artifactPath = GetArtifactPath(
            book,
            chapterId);

        if (!File.Exists(artifactPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            artifactPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        ChapterProductionArtifact artifact;

        try
        {
            artifact =
                await JsonSerializer
                    .DeserializeAsync<ChapterProductionArtifact>(
                        stream,
                        SerializerOptions,
                        cancellationToken) ??
                throw new InvalidDataException(
                    $"Production script is empty: {artifactPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Production script contains invalid JSON: {artifactPath}",
                exception);
        }

        ValidateArtifact(
            book,
            artifact,
            chapterId,
            artifactPath);

        return artifact;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        BookProject book,
        ChapterProductionArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(artifact);

        ValidateChapterId(artifact.Script.ChapterId);

        var artifactPath = GetArtifactPath(
            book,
            artifact.Script.ChapterId);

        ValidateArtifact(
            book,
            artifact,
            artifact.Script.ChapterId,
            artifactPath);

        var scriptsDirectory =
            Path.GetDirectoryName(artifactPath) ??
            throw new InvalidDataException(
                $"Production script has no parent directory: " +
                artifactPath);

        Directory.CreateDirectory(scriptsDirectory);

        var temporaryPath = Path.Combine(
            scriptsDirectory,
            $".{artifact.Script.ChapterId}-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    artifact,
                    SerializerOptions,
                    cancellationToken);

                await stream.WriteAsync(
                    "\n"u8.ToArray(),
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(artifactPath))
            {
                File.Replace(
                    temporaryPath,
                    artifactPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    artifactPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetArtifactPath(
        BookProject book,
        string chapterId)
    {
        return Path.Combine(
            book.RootPath,
            "production",
            "scripts",
            $"{chapterId}.json");
    }

    private static void ValidateArtifact(
        BookProject book,
        ChapterProductionArtifact artifact,
        string expectedChapterId,
        string artifactPath)
    {
        if (artifact.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Production-script schema version " +
                $"{artifact.SchemaVersion} is unsupported: {artifactPath}");
        }

        if (!string.Equals(
                artifact.BookId,
                book.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production script belongs to book " +
                $"'{artifact.BookId}', not '{book.Id}': {artifactPath}");
        }

        if (!string.Equals(
                artifact.Script.ChapterId,
                expectedChapterId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Production script contains chapter ID " +
                $"'{artifact.Script.ChapterId}', expected " +
                $"'{expectedChapterId}': {artifactPath}");
        }

        if (string.IsNullOrWhiteSpace(artifact.SourceSha256))
        {
            throw new InvalidDataException(
                $"Production script has no source hash: {artifactPath}");
        }

        if (string.IsNullOrWhiteSpace(artifact.PreparationVersion))
        {
            throw new InvalidDataException(
                $"Production script has no preparation version: " +
                artifactPath);
        }

        var validationErrors =
            new ProductionScriptValidator().Validate(
                artifact.Script);

        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException(
                $"Production script failed exact-text validation: " +
                $"{string.Join(" | ", validationErrors)}");
        }

        var unassignedDialogue = artifact.Script.Segments
            .Where(segment => segment.Kind == SegmentKind.Dialogue)
            .FirstOrDefault(
                segment => string.Equals(
                    segment.SpeakerId,
                    ProductionScriptSegmenter.UnassignedSpeakerId,
                    StringComparison.OrdinalIgnoreCase));

        if (unassignedDialogue is not null)
        {
            throw new InvalidDataException(
                $"Production script contains unassigned dialogue segment " +
                $"{unassignedDialogue.Index}: {artifactPath}");
        }
    }

    private static void ValidateChapterId(string chapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);

        if (!ChapterIdRegex().IsMatch(chapterId))
        {
            throw new ArgumentException(
                $"Invalid chapter ID: {chapterId}",
                nameof(chapterId));
        }
    }

    [GeneratedRegex(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChapterIdRegex();
}
