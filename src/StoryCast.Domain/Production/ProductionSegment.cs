namespace StoryCast.Domain.Production;

/// <summary>
/// Represents one speaker-attributed portion of a chapter.
/// </summary>
public sealed class ProductionSegment
{
    /// <summary>
    /// Gets the zero-based position of the segment in the chapter.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the zero-based source-text offset at which the segment begins.
    /// </summary>
    public required int SourceStart { get; init; }

    /// <summary>
    /// Gets the number of source characters covered by the segment.
    /// </summary>
    public required int SourceLength { get; init; }

    /// <summary>
    /// Gets the exact text copied from the source chapter.
    /// </summary>
    public required string SourceText { get; init; }

    /// <summary>
    /// Gets the stable identifier of the assigned speaker.
    /// </summary>
    public required string SpeakerId { get; init; }

    /// <summary>
    /// Gets the narrative function of the segment.
    /// </summary>
    public required SegmentKind Kind { get; init; }

    /// <summary>
    /// Gets an optional description of how the segment should be delivered.
    /// </summary>
    public string Delivery { get; init; } = string.Empty;

    /// <summary>
    /// Gets the confidence of an automated dialogue attribution.
    /// </summary>
    public decimal? AttributionConfidence { get; init; }

    /// <summary>
    /// Gets the explanation for an automated dialogue attribution.
    /// </summary>
    public string AttributionRationale { get; init; } = string.Empty;
}
