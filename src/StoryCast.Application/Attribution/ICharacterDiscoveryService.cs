using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Discovers speaking and narrating characters in prepared chapter text.
/// </summary>
public interface ICharacterDiscoveryService
{
    /// <summary>
    /// Discovers character identities mentioned in one chapter.
    /// </summary>
    /// <param name="chapter">The prepared chapter.</param>
    /// <param name="knownCharacters">
    /// Characters already established by earlier chapters.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel generation.
    /// </param>
    /// <returns>
    /// Character profiles discovered or reaffirmed by the chapter.
    /// </returns>
    Task<IReadOnlyList<CharacterProfile>> DiscoverAsync(
        PreparedChapter chapter,
        IReadOnlyList<CharacterProfile> knownCharacters,
        CancellationToken cancellationToken = default);
}
