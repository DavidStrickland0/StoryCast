namespace StoryCast.Domain.Characters;

/// <summary>
/// Describes a character discovered in a manuscript.
/// </summary>
public sealed class CharacterProfile
{
    /// <summary>
    /// Gets the stable identifier used for the character throughout the book.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the character's preferred display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets names, titles, and descriptions that refer to the character.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// Gets the character description inferred from the manuscript.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets traits relevant to selecting an appropriate voice.
    /// </summary>
    public IReadOnlyList<string> VoiceTraits { get; init; } = [];

    /// <summary>
    /// Gets the presentation preferred for the character's voice.
    /// </summary>
    public string VoicePresentation { get; init; } = "unspecified";
    /// <summary>
    /// Gets a value indicating whether the manuscript gives the
    /// character a proper name or identity-specific title.
    /// </summary>
    public bool IsNamed { get; init; } = true;

    /// <summary>
    /// Gets the character's narrative importance.
    /// </summary>
    public CharacterImportance Importance { get; init; }

    /// <summary>
    /// Gets a value indicating whether the character narrates some or all of the book.
    /// </summary>
    public bool IsNarrator { get; init; }
}
