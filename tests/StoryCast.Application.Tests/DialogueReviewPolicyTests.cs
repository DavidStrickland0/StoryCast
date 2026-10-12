using StoryCast.Application.Production;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Tests;

/// <summary>Tests unattended dialogue review decisions.</summary>
public sealed class DialogueReviewPolicyTests
{
    [Theory]
    [InlineData(null, "adam", true)]
    [InlineData(0.79, "adam", true)]
    [InlineData(0.8, "adam", false)]
    [InlineData(0.99, "spector", false)]
    [InlineData(1.01, "adam", true)]
    [InlineData(0.99, "narrator", true)]
    [InlineData(0.99, "unknown", true)]
    [InlineData(0.99, "", true)]
    /// <summary>Tests confidence boundaries and unresolved speakers.</summary>
    /// <param name="confidence">The declared confidence.</param>
    /// <param name="speaker">The assigned speaker.</param>
    /// <param name="review">Whether review is required.</param>
    public void QueuesOnlyUncertainDialogue(double? confidence, string speaker, bool review)
    {
        var segment = new ProductionSegment
        {
            Index = 3, SourceStart = 0, SourceLength = 5, SourceText = "Hello",
            Kind = SegmentKind.Dialogue, SpeakerId = speaker,
            AttributionConfidence = confidence is null ? null : (decimal)confidence.Value
        };
        Assert.Equal(review, DialogueReviewPolicy.GetUncertainSegments([segment]).Contains(3));
    }

    [Fact]
    /// <summary>Narration does not require a dialogue confidence score.</summary>
    public void NarrationDoesNotRequireAnAttributionConfidence()
    {
        var segment = new ProductionSegment
        {
            Index = 0, SourceStart = 0, SourceLength = 5, SourceText = "Hello",
            Kind = SegmentKind.Narration, SpeakerId = "narrator"
        };
        Assert.Empty(DialogueReviewPolicy.GetUncertainSegments([segment]));
    }
}
