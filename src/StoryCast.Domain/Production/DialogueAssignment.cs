namespace StoryCast.Domain.Production;

/// <summary>
/// Represents a generated speaker assignment for one dialogue segment.
/// </summary>
public sealed class DialogueAssignment
{
    /// <summary>
    /// Gets the production-segment index being assigned.
    /// </summary>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// Gets the stable character identifier assigned to the dialogue.
    /// </summary>
    public required string SpeakerId { get; init; }

    /// <summary>
    /// Gets the attribution confidence from zero through one.
    /// </summary>
    public required decimal Confidence { get; init; }

    /// <summary>
    /// Gets an optional description of the intended delivery.
    /// </summary>
    public string Delivery { get; init; } = string.Empty;

    /// <summary>
    /// Gets a concise explanation of the speaker assignment.
    /// </summary>
    public string Rationale { get; init; } = string.Empty;
}
