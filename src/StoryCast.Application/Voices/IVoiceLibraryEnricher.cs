namespace StoryCast.Application.Voices;

/// <summary>
/// Enriches voice manifests using reliable filesystem metadata.
/// </summary>
public interface IVoiceLibraryEnricher
{
    /// <summary>
    /// Adds metadata derived from voice-directory naming conventions.
    /// </summary>
    /// <param name="libraryPath">The voice-library directory.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>A summary of the enrichment operation.</returns>
    Task<VoiceLibraryEnrichmentResult> EnrichAsync(
        string libraryPath,
        CancellationToken cancellationToken = default);
}
