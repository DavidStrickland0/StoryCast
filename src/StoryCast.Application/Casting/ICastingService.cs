using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>
/// Automatically assigns available voices to audiobook roles.
/// </summary>
public interface ICastingService
{
    /// <summary>
    /// Assigns one unique voice to the narrator and every character.
    /// </summary>
    /// <param name="registry">The established character registry.</param>
    /// <param name="voices">The eligible voice profiles.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The generated casting assignments.</returns>
    Task<IReadOnlyList<CastingAssignment>> AssignAsync(
        CharacterRegistry registry,
        IReadOnlyList<VoiceProfile> voices,
        CancellationToken cancellationToken = default);
}
