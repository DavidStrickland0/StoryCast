using StoryCast.Domain.Voices;

namespace StoryCast.Application.Voices;

/// <summary>
/// Provides the voices available for audiobook production.
/// </summary>
public interface IVoiceLibrary
{
    /// <summary>
    /// Loads and validates the available voices.
    /// </summary>
    /// <param name="libraryPath">
    /// The directory containing one subdirectory for each voice.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The validated voice profiles.</returns>
    Task<IReadOnlyList<VoiceProfile>> LoadAsync(
        string libraryPath,
        CancellationToken cancellationToken = default);
}
