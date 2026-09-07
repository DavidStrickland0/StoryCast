namespace StoryCast.Domain.Production;

/// <summary>
/// Contains the verified speaker-attribution script for one chapter.
/// </summary>
public sealed class ChapterProductionScript
{
    /// <summary>
    /// Gets the stable chapter identifier.
    /// </summary>
    public required string ChapterId { get; init; }

    /// <summary>
    /// Gets the complete source text from which the script was produced.
    /// </summary>
    public required string SourceText { get; init; }

    /// <summary>
    /// Gets the source segments in their original order.
    /// </summary>
    public required IReadOnlyList<ProductionSegment> Segments { get; init; }
}
