using StoryCast.Domain.Voices;

namespace StoryCast.Application.Voices;

/// <summary>
/// Measures objective properties of voice-reference audio.
/// </summary>
public interface IVoiceSampleAnalyzer
{
    /// <summary>
    /// Analyzes one voice-reference sample.
    /// </summary>
    /// <param name="voice">The voice profile to analyze.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The measured audio properties.</returns>
    Task<VoiceSampleAnalysis> AnalyzeAsync(
        VoiceProfile voice,
        CancellationToken cancellationToken = default);
}
