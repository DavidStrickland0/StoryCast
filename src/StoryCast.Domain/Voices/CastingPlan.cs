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

    /// <summary>Previously released assignments, retained for audit.</summary>
    public IReadOnlyList<CastingAssignment> RetiredAssignments { get; init; } = [];

    /// <summary>Attributed segment counts by chapter and voice; replaced on retries.</summary>
    public IReadOnlyDictionary<string, ChapterVoiceUsage> ChapterVoiceUsage { get; init; } =
        new Dictionary<string, ChapterVoiceUsage>();

    /// <summary>Total attributed speech segments per voice across recorded chapters.</summary>
    public IReadOnlyDictionary<string, int> VoiceUseCounts { get; init; } = new Dictionary<string, int>();
}
