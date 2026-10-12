using StoryCast.Domain.Production;

namespace StoryCast.Application.Production;

/// <summary>Identifies uncertain dialogue before unattended synthesis.</summary>
public static class DialogueReviewPolicy
{
    /// <summary>Returns dialogue indexes needing a source review.</summary>
    /// <param name="segments">The attributed script segments.</param>
    /// <returns>Indexes of missing, unresolved, or low-confidence assignments.</returns>
    public static IReadOnlyList<int> GetUncertainSegments(IEnumerable<ProductionSegment> segments) =>
        segments.Where(segment => segment.Kind == SegmentKind.Dialogue &&
            (segment.AttributionConfidence is null or < 0.8m or > 1m ||
             string.IsNullOrWhiteSpace(segment.SpeakerId) ||
             segment.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase) ||
             segment.SpeakerId.Equals("unknown", StringComparison.OrdinalIgnoreCase)))
        .Select(segment => segment.Index).ToArray();
}
