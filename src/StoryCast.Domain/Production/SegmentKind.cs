namespace StoryCast.Domain.Production;

/// <summary>
/// Identifies the narrative function of a production segment.
/// </summary>
public enum SegmentKind
{
    /// <summary>
    /// Prose spoken by the book's narrator.
    /// </summary>
    Narration,

    /// <summary>
    /// Words spoken directly by a character.
    /// </summary>
    Dialogue
}
