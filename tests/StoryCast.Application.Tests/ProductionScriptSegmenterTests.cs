using StoryCast.Application.Attribution;
using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests deterministic narration and dialogue segmentation.
/// </summary>
public sealed class ProductionScriptSegmenterTests
{
    /// <summary>
    /// Verifies segmentation around straight quotation marks.
    /// </summary>
    [Fact]
    public void Segment_SplitsStraightQuotedDialogue()
    {
        const string text =
            "Miller looked up. \"We need to move.\" She stood.";

        var chapter = CreateChapter(text);
        var segmenter = new ProductionScriptSegmenter();

        var script = segmenter.Segment(chapter);

        Assert.Equal(3, script.Segments.Count);

        Assert.Equal(
            SegmentKind.Narration,
            script.Segments[0].Kind);

        Assert.Equal(
            "Miller looked up. ",
            script.Segments[0].SourceText);

        Assert.Equal(
            SegmentKind.Dialogue,
            script.Segments[1].Kind);

        Assert.Equal(
            "\"We need to move.\"",
            script.Segments[1].SourceText);

        Assert.Equal(
            ProductionScriptSegmenter.UnassignedSpeakerId,
            script.Segments[1].SpeakerId);

        Assert.Equal(
            " She stood.",
            script.Segments[2].SourceText);
    }

    /// <summary>
    /// Verifies segmentation around typographic quotation marks.
    /// </summary>
    [Fact]
    public void Segment_SplitsTypographicQuotedDialogue()
    {
        const string text =
            "Miller said, \u201CMove now.\u201D Jensen nodded.";

        var chapter = CreateChapter(text);
        var segmenter = new ProductionScriptSegmenter();

        var script = segmenter.Segment(chapter);

        Assert.Equal(3, script.Segments.Count);
        Assert.Equal(
            "\u201CMove now.\u201D",
            script.Segments[1].SourceText);
        Assert.Equal(
            SegmentKind.Dialogue,
            script.Segments[1].Kind);
    }

    /// <summary>
    /// Verifies that unclosed quotation marks remain narration.
    /// </summary>
    [Fact]
    public void Segment_TreatsUnclosedQuoteAsNarration()
    {
        const string text =
            "The inscription began, \"Do not enter.";

        var chapter = CreateChapter(text);
        var segmenter = new ProductionScriptSegmenter();

        var script = segmenter.Segment(chapter);

        Assert.Equal(2, script.Segments.Count);
        Assert.All(
            script.Segments,
            segment => Assert.Equal(
                SegmentKind.Narration,
                segment.Kind));
    }

    /// <summary>
    /// Verifies exact source preservation after segmentation.
    /// </summary>
    [Fact]
    public void Segment_PassesProductionScriptValidation()
    {
        const string text =
            "Miller said, â€œMove.â€\n\nâ€œWhere?â€ Jensen asked.";

        var chapter = CreateChapter(text);
        var segmenter = new ProductionScriptSegmenter();
        var validator = new ProductionScriptValidator();

        var script = segmenter.Segment(chapter);
        var errors = validator.Validate(script);

        Assert.Empty(errors);

        Assert.Equal(
            text,
            string.Concat(
                script.Segments.Select(
                    segment => segment.SourceText)));
    }

    /// <summary>
    /// Verifies that paragraph separators between dialogue segments are
    /// retained without becoming standalone unspoken segments.
    /// </summary>
    [Fact]
    public void Segment_MergesWhitespaceOnlyNarration()
    {
        const string text =
            "Miller arrived. \u201CHold.\u201D\n\n" +
            "\u201CWait,\u201D Tower replied.";

        var chapter = CreateChapter(text);
        var segmenter = new ProductionScriptSegmenter();
        var validator = new ProductionScriptValidator();

        var script = segmenter.Segment(chapter);
        var errors = validator.Validate(script);

        Assert.Empty(errors);
        Assert.Equal(4, script.Segments.Count);
        Assert.DoesNotContain(
            script.Segments,
            segment => string.IsNullOrWhiteSpace(
                segment.SourceText));
        Assert.Equal(
            "\u201CHold.\u201D\n\n",
            script.Segments[1].SourceText);
        Assert.Equal(
            text,
            string.Concat(
                script.Segments.Select(
                    segment => segment.SourceText)));
    }
    private static PreparedChapter CreateChapter(string text)
    {
        return new PreparedChapter
        {
            ChapterId = "chapter-001",
            SourcePath = @"C:\Book\chapter-001.md",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            SpokenText = text
        };
    }
}
