using StoryCast.Domain.Production;

namespace StoryCast.Domain.Tests.Production;

/// <summary>
/// Tests exact-source validation of speaker-attributed scripts.
/// </summary>
public sealed class ProductionScriptValidatorTests
{
    /// <summary>
    /// Verifies that contiguous, unchanged segments are accepted.
    /// </summary>
    [Fact]
    public void Validate_AcceptsExactSourceCoverage()
    {
        const string source =
            "Miller looked up. \"We need to move.\"";

        var script = new ChapterProductionScript
        {
            ChapterId = "chapter-001",
            SourceText = source,
            Segments =
            [
                new ProductionSegment
                {
                    Index = 0,
                    SourceStart = 0,
                    SourceLength = 18,
                    SourceText = "Miller looked up. ",
                    SpeakerId = "narrator",
                    Kind = SegmentKind.Narration
                },
                new ProductionSegment
                {
                    Index = 1,
                    SourceStart = 18,
                    SourceLength = 18,
                    SourceText = "\"We need to move.\"",
                    SpeakerId = "marcia-miller",
                    Kind = SegmentKind.Dialogue
                }
            ]
        };

        var validator = new ProductionScriptValidator();

        var errors = validator.Validate(script);

        Assert.Empty(errors);
    }

    /// <summary>
    /// Verifies that omitted source text is rejected.
    /// </summary>
    [Fact]
    public void Validate_RejectsMissingSourceText()
    {
        const string source =
            "Miller looked up. \"We need to move.\"";

        var script = new ChapterProductionScript
        {
            ChapterId = "chapter-001",
            SourceText = source,
            Segments =
            [
                new ProductionSegment
                {
                    Index = 0,
                    SourceStart = 0,
                    SourceLength = 18,
                    SourceText = "Miller looked up. ",
                    SpeakerId = "narrator",
                    Kind = SegmentKind.Narration
                }
            ]
        };

        var validator = new ProductionScriptValidator();

        var errors = validator.Validate(script);

        Assert.Contains(
            errors,
            error => error.Contains(
                "Segments cover",
                StringComparison.Ordinal));
    }
}
