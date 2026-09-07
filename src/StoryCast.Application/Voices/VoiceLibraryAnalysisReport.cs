namespace StoryCast.Application.Voices;

/// <summary>
/// Contains objective sample measurements for a voice library.
/// </summary>
public sealed class VoiceLibraryAnalysisReport
{
    /// <summary>
    /// Gets the report schema version.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the UTC time at which analysis completed.
    /// </summary>
    public required DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>
    /// Gets the analyzed voice samples.
    /// </summary>
    public required IReadOnlyList<VoiceSampleAnalysis> Voices { get; init; }
}
