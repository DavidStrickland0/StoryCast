using System.Text.Json;
using StoryCast.Application.Voices;
using StoryCast.Domain.Voices;

namespace StoryCast.Infrastructure.Voices;

/// <summary>
/// Loads voice profiles from directories on the local filesystem.
/// </summary>
public sealed class FileSystemVoiceLibrary : IVoiceLibrary
{
    private const string ManifestFileName = "voice.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceProfile>> LoadAsync(
        string libraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        var fullLibraryPath = Path.GetFullPath(libraryPath);

        if (!Directory.Exists(fullLibraryPath))
        {
            throw new DirectoryNotFoundException(
                $"Voice library directory was not found: {fullLibraryPath}");
        }

        var voices = new List<VoiceProfile>();
        var voiceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var voiceDirectory in Directory
                     .EnumerateDirectories(fullLibraryPath)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(
                voiceDirectory,
                ManifestFileName);

            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    $"Voice directory does not contain {ManifestFileName}: " +
                    voiceDirectory);
            }

            await using var manifestStream = File.OpenRead(manifestPath);

            var manifest = await JsonSerializer.DeserializeAsync<VoiceManifest>(
                manifestStream,
                SerializerOptions,
                cancellationToken);

            if (manifest is null)
            {
                throw new InvalidDataException(
                    $"Voice manifest could not be read: {manifestPath}");
            }

            ValidateManifest(manifest, manifestPath);

            if (!voiceIds.Add(manifest.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate voice ID '{manifest.Id}' was found.");
            }

            var samplePath = ResolveSamplePath(
                voiceDirectory,
                manifest.Sample,
                manifestPath);

            voices.Add(
                new VoiceProfile
                {
                    Id = manifest.Id,
                    SamplePath = samplePath,
                    Language = manifest.Language,
                    Accent = manifest.Accent,
                    ApparentAge = manifest.ApparentAge,
                    Presentation = manifest.Presentation,
                    Qualities = manifest.Qualities,
                    SuitableRoles = manifest.SuitableRoles,
                    NarratorSuitable = manifest.NarratorSuitable
                });
        }

        return voices;
    }

    private static void ValidateManifest(
        VoiceManifest manifest,
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException(
                $"Voice manifest is missing an ID: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sample))
        {
            throw new InvalidDataException(
                $"Voice manifest is missing a sample filename: {manifestPath}");
        }
    }

    private static string ResolveSamplePath(
        string voiceDirectory,
        string sample,
        string manifestPath)
    {
        var fullVoiceDirectory = Path.GetFullPath(voiceDirectory);
        var samplePath = Path.GetFullPath(
            Path.Combine(fullVoiceDirectory, sample));

        var relativePath = Path.GetRelativePath(
            fullVoiceDirectory,
            samplePath);

        if (relativePath == ".." ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Voice sample must remain inside its voice directory: " +
                manifestPath);
        }

        if (!File.Exists(samplePath))
        {
            throw new FileNotFoundException(
                $"Voice sample was not found for manifest: {manifestPath}",
                samplePath);
        }

        return samplePath;
    }

    private sealed class VoiceManifest
    {
        public string Id { get; init; } = string.Empty;

        public string Sample { get; init; } = string.Empty;

        public string Language { get; init; } = "en";

        public string Accent { get; init; } = string.Empty;

        public string ApparentAge { get; init; } = string.Empty;

        public string Presentation { get; init; } = string.Empty;

        public IReadOnlyList<string> Qualities { get; init; } = [];

        public IReadOnlyList<string> SuitableRoles { get; init; } = [];

        public bool NarratorSuitable { get; init; }
    }
}
