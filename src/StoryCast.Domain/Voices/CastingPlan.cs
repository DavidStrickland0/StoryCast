namespace StoryCast.Domain.Voices;

/// <summary>
/// Contains the persistent voice assignments for one book.
/// </summary>
public sealed class CastingPlan
{
    /// <summary>
    /// Gets the casting-plan schema version.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the stable identifier of the owning book.
    /// </summary>
    public required string BookId { get; init; }

    /// <summary>
    /// Gets the narrator and character voice assignments.
    /// </summary>
    public required IReadOnlyList<CastingAssignment> Assignments { get; init; }
}
