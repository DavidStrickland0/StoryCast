using System.Text.Json;
using StoryCast.Application.Voices;
using StoryCast.Domain.Voices;

namespace StoryCast.Infrastructure.Voices;

/// <summary>
/// Filters filesystem voices through the latest verification report.
/// </summary>
public sealed class FileSystemVerifiedVoiceLibrary
    : IVerifiedVoiceLibrary
{
    private const int SupportedSchemaVersion = 1;
    private const string VerificationFileName =
        "voice-verification.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly IVoiceLibrary voiceLibrary;

    /// <summary>
    /// Initializes a verified filesystem voice library.
    /// </summary>
    /// <param name="voiceLibrary">
    /// The underlying validated voice library.
    /// </param>
    public FileSystemVerifiedVoiceLibrary(
        IVoiceLibrary voiceLibrary)
    {
        this.voiceLibrary = voiceLibrary ??
            throw new ArgumentNullException(nameof(voiceLibrary));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceProfile>> LoadAsync(
        string libraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        var fullLibraryPath = Path.GetFullPath(
            libraryPath);

        var voices = await voiceLibrary.LoadAsync(
            fullLibraryPath,
            cancellationToken);

        var reportPath = Path.Combine(
            fullLibraryPath,
            VerificationFileName);

        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException(
                "Voice verification must be completed before casting: " +
                reportPath,
                reportPath);
        }

        VerificationReport report;

        await using (var stream = new FileStream(
                         reportPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            try
            {
                report =
                    await JsonSerializer
                        .DeserializeAsync<VerificationReport>(
                            stream,
                            SerializerOptions,
                            cancellationToken) ??
                    throw new InvalidDataException(
                        $"Voice verification report is empty: " +
                        reportPath);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Voice verification report contains invalid JSON: " +
                    reportPath,
                    exception);
            }
        }

        if (report.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Voice-verification schema version " +
                $"{report.SchemaVersion} is unsupported: {reportPath}");
        }

        var resultsByVoiceId =
            new Dictionary<string, VerificationResult>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var result in report.Voices)
        {
            if (!resultsByVoiceId.TryAdd(
                    result.VoiceId,
                    result))
            {
                throw new InvalidDataException(
                    $"Voice verification contains duplicate voice ID " +
                    $"'{result.VoiceId}': {reportPath}");
            }
        }

        var eligibleVoices = new List<VoiceProfile>();

        foreach (var voice in voices)
        {
            if (!resultsByVoiceId.TryGetValue(
                    voice.Id,
                    out var result))
            {
                throw new InvalidDataException(
                    $"Voice '{voice.Id}' is missing from the verification " +
                    $"report. Run voices verify again.");
            }

            if (string.Equals(
                    result.Status,
                    "pass",
                    StringComparison.OrdinalIgnoreCase))
            {
                eligibleVoices.Add(voice);
            }
        }

        if (eligibleVoices.Count == 0)
        {
            throw new InvalidDataException(
                "No voices passed speech verification.");
        }

        return eligibleVoices;
    }

    private sealed class VerificationReport
    {
        public int SchemaVersion { get; init; }

        public IReadOnlyList<VerificationResult>
            Voices
        { get; init; } = [];
    }

    private sealed class VerificationResult
    {
        public string VoiceId { get; init; } =
            string.Empty;

        public string Status { get; init; } =
            string.Empty;
    }
}
