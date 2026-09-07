namespace StoryCast.Application.Attribution;

/// <summary>
/// Summarizes dialogue attribution performed for a book.
/// </summary>
public sealed class DialogueAttributionWorkflowResult
{
    /// <summary>
    /// Gets the number of chapters newly processed.
    /// </summary>
    public required int ProcessedChapters { get; init; }

    /// <summary>
    /// Gets the number of unchanged chapters skipped.
    /// </summary>
    public required int SkippedChapters { get; init; }

    /// <summary>
    /// Gets the total number of dialogue segments encountered.
    /// </summary>
    public required int DialogueSegments { get; init; }

    /// <summary>
    /// Gets the number of assignments with confidence below 0.60.
    /// </summary>
    public required int LowConfidenceAssignments { get; init; }
}
