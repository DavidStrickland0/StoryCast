namespace StoryCast.Application.Voices;

/// <summary>
/// Summarizes metadata enrichment performed on a voice library.
/// </summary>
public sealed class VoiceLibraryEnrichmentResult
{
    /// <summary>
    /// Gets the number of voice manifests that were updated.
    /// </summary>
    public required int UpdatedVoices { get; init; }

    /// <summary>
    /// Gets the number of voice manifests that required no changes.
    /// </summary>
    public required int UnchangedVoices { get; init; }

    /// <summary>
    /// Gets the number of directories whose names provided no known metadata.
    /// </summary>
    public required int UnrecognizedVoices { get; init; }
}
