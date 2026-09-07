using StoryCast.Domain.Voices;

namespace StoryCast.Application.Voices;

/// <summary>
/// Loads voices that passed the latest speech-verification report.
/// </summary>
public interface IVerifiedVoiceLibrary
{
    /// <summary>
    /// Loads voice profiles whose verification status is pass.
    /// </summary>
    /// <param name="libraryPath">The voice-library directory.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The verified eligible voices.</returns>
    Task<IReadOnlyList<VoiceProfile>> LoadAsync(
        string libraryPath,
        CancellationToken cancellationToken = default);
}
