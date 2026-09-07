using StoryCast.Domain.Characters;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Assigns established character identities to dialogue segments.
/// </summary>
public interface IDialogueAttributionService
{
    /// <summary>
    /// Attributes every dialogue segment in one chapter.
    /// </summary>
    /// <param name="script">The unattributed production script.</param>
    /// <param name="registry">The established character registry.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel generation.
    /// </param>
    /// <returns>The generated dialogue assignments.</returns>
    Task<IReadOnlyList<DialogueAssignment>> AttributeAsync(
        ChapterProductionScript script,
        CharacterRegistry registry,
        CancellationToken cancellationToken = default);
}
