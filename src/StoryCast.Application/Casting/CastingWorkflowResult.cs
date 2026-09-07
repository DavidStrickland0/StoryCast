namespace StoryCast.Application.Casting;

/// <summary>
/// Summarizes automatic casting for one book.
/// </summary>
public sealed class CastingWorkflowResult
{
    /// <summary>
    /// Gets the number of eligible verified voices.
    /// </summary>
    public required int EligibleVoiceCount { get; init; }

    /// <summary>
    /// Gets the number of persisted role assignments.
    /// </summary>
    public required int AssignmentCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether an existing plan was reused.
    /// </summary>
    public required bool ReusedExistingPlan { get; init; }
}
