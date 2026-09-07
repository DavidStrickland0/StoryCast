namespace StoryCast.Domain.Characters;

/// <summary>
/// Describes the narrative importance of a character.
/// </summary>
public enum CharacterImportance
{
    /// <summary>
    /// The character appears briefly and does not require a unique voice.
    /// </summary>
    Minor,

    /// <summary>
    /// The character recurs or has meaningful dialogue.
    /// </summary>
    Supporting,

    /// <summary>
    /// The character is central to the story.
    /// </summary>
    Major
}
