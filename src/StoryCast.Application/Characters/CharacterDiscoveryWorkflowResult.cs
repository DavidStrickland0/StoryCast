namespace StoryCast.Application.Characters;

/// <summary>
/// Summarizes one complete character-discovery workflow.
/// </summary>
public sealed class CharacterDiscoveryWorkflowResult
{
    /// <summary>
    /// Gets the number of chapters sent for character discovery.
    /// </summary>
    public required int ProcessedChapters { get; init; }

    /// <summary>
    /// Gets the number of unchanged chapters skipped.
    /// </summary>
    public required int SkippedChapters { get; init; }

    /// <summary>
    /// Gets the number of characters in the resulting registry.
    /// </summary>
    public required int CharacterCount { get; init; }
}
