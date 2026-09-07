using System.Text.Json;
using System.Text.Json.Nodes;
using StoryCast.Application.Voices;

namespace StoryCast.Infrastructure.Voices;

/// <summary>
/// Safely enriches voice manifests from voice-directory names.
/// </summary>
public sealed class FileSystemVoiceLibraryEnricher
    : IVoiceLibraryEnricher
{
    private const string ManifestFileName = "voice.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true
        };

    /// <inheritdoc />
    public async Task<VoiceLibraryEnrichmentResult> EnrichAsync(
        string libraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        var fullLibraryPath = Path.GetFullPath(libraryPath);

        if (!Directory.Exists(fullLibraryPath))
        {
            throw new DirectoryNotFoundException(
                $"Voice library directory was not found: " +
                fullLibraryPath);
        }

        var updatedVoices = 0;
        var unchangedVoices = 0;
        var unrecognizedVoices = 0;

        foreach (var voiceDirectory in Directory
                     .EnumerateDirectories(fullLibraryPath)
                     .OrderBy(
                         path => path,
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(
                voiceDirectory);

            var derivedMetadata = DeriveMetadata(
                directoryName);

            if (derivedMetadata is null)
            {
                unrecognizedVoices++;
                continue;
            }

            var manifestPath = Path.Combine(
                voiceDirectory,
                ManifestFileName);

            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    $"Voice directory does not contain " +
                    $"{ManifestFileName}: {voiceDirectory}");
            }

            var manifest = await LoadManifestAsync(
                manifestPath,
                cancellationToken);

            var changed = ApplyMetadata(
                manifest,
                derivedMetadata);

            if (!changed)
            {
                unchangedVoices++;
                continue;
            }

            await SaveManifestAsync(
                manifestPath,
                manifest,
                cancellationToken);

            updatedVoices++;
        }

        return new VoiceLibraryEnrichmentResult
        {
            UpdatedVoices = updatedVoices,
            UnchangedVoices = unchangedVoices,
            UnrecognizedVoices = unrecognizedVoices
        };
    }

    private static DerivedVoiceMetadata? DeriveMetadata(
        string directoryName)
    {
        if (directoryName.StartsWith(
                "Male-",
                StringComparison.OrdinalIgnoreCase))
        {
            return new DerivedVoiceMetadata(
                "male",
                []);
        }

        if (directoryName.StartsWith(
                "Female-",
                StringComparison.OrdinalIgnoreCase))
        {
            return new DerivedVoiceMetadata(
                "female",
                []);
        }

        if (directoryName.StartsWith(
                "Whisper-",
                StringComparison.OrdinalIgnoreCase))
        {
            return new DerivedVoiceMetadata(
                string.Empty,
                ["whispered"]);
        }

        return null;
    }

    private static async Task<JsonObject> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        try
        {
            var node = await JsonNode.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            return node as JsonObject ??
                throw new InvalidDataException(
                    $"Voice manifest must contain a JSON object: " +
                    manifestPath);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Voice manifest contains invalid JSON: " +
                manifestPath,
                exception);
        }
    }

    private static bool ApplyMetadata(
        JsonObject manifest,
        DerivedVoiceMetadata metadata)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(metadata.Presentation) &&
            IsMissingText(manifest["presentation"]))
        {
            manifest["presentation"] =
                metadata.Presentation;

            changed = true;
        }

        if (metadata.Qualities.Count == 0)
        {
            return changed;
        }

        var qualities = manifest["qualities"] as JsonArray;

        if (qualities is null)
        {
            qualities = [];
            manifest["qualities"] = qualities;
            changed = true;
        }

        foreach (var quality in metadata.Qualities)
        {
            var alreadyPresent = qualities.Any(
                value => value is JsonValue jsonValue &&
                    jsonValue.TryGetValue<string>(
                        out var existingQuality) &&
                    string.Equals(
                        existingQuality,
                        quality,
                        StringComparison.OrdinalIgnoreCase));

            if (alreadyPresent)
            {
                continue;
            }

            qualities.Add(quality);
            changed = true;
        }

        return changed;
    }

    private static bool IsMissingText(JsonNode? node)
    {
        return node is null ||
            node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            string.IsNullOrWhiteSpace(text);
    }

    private static async Task SaveManifestAsync(
        string manifestPath,
        JsonObject manifest,
        CancellationToken cancellationToken)
    {
        var parentDirectory =
            Path.GetDirectoryName(manifestPath) ??
            throw new InvalidDataException(
                $"Voice manifest has no parent directory: " +
                manifestPath);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".voice-{Guid.NewGuid():N}.tmp");

        try
        {
            var json =
                manifest.ToJsonString(SerializerOptions) +
                Environment.NewLine;

            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            File.Replace(
                temporaryPath,
                manifestPath,
                destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record DerivedVoiceMetadata(
        string Presentation,
        IReadOnlyList<string> Qualities);
}
