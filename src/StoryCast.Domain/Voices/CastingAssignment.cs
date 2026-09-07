namespace StoryCast.Domain.Voices;

/// <summary>
/// Records the permanent assignment of a voice to a character within a book.
/// </summary>
public sealed class CastingAssignment
{
    /// <summary>
    /// Gets the stable character identifier.
    /// </summary>
    public required string CharacterId { get; init; }

    /// <summary>
    /// Gets the assigned voice identifier.
    /// </summary>
    public required string VoiceId { get; init; }

    /// <summary>
    /// Gets the casting confidence from zero through one.
    /// </summary>
    public decimal Confidence { get; init; }

    /// <summary>
    /// Gets the explanation for the casting decision.
    /// </summary>
    public string Rationale { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the assignment was manually locked.
    /// </summary>
    public bool IsLocked { get; init; }
}
