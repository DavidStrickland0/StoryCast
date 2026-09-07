using StoryCast.Application.Attribution;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests validation and application of dialogue-speaker assignments.
/// </summary>
public sealed class DialogueAttributionApplicatorTests
{
    /// <summary>
    /// Verifies that complete valid assignments are applied.
    /// </summary>
    [Fact]
    public void Apply_AttributesEveryDialogueSegment()
    {
        var script = CreateScript();
        var registry = CreateRegistry();
        var applicator = new DialogueAttributionApplicator();

        var result = applicator.Apply(
            script,
            registry,
            [
                new DialogueAssignment
                {
                    SegmentIndex = 1,
                    SpeakerId = "marcia-miller",
                    Confidence = 0.98m,
                    Delivery = "firm",
                    Rationale = "Miller is identified by the dialogue tag."
                },
                new DialogueAssignment
                {
                    SegmentIndex = 3,
                    SpeakerId = "elias-thorne",
                    Confidence = 0.91m,
                    Delivery = "controlled",
                    Rationale = "Thorne answers Miller."
                }
            ]);

        Assert.Equal(
            "marcia-miller",
            result.Segments[1].SpeakerId);

        Assert.Equal(
            "elias-thorne",
            result.Segments[3].SpeakerId);

        Assert.Equal(
            0.98m,
            result.Segments[1].AttributionConfidence);

        Assert.Equal(
            script.SourceText,
            string.Concat(
                result.Segments.Select(
                    segment => segment.SourceText)));
    }

    /// <summary>
    /// Verifies that missing dialogue assignments are rejected.
    /// </summary>
    [Fact]
    public void Apply_RejectsMissingAssignment()
    {
        var applicator = new DialogueAttributionApplicator();

        Assert.Throws<InvalidDataException>(
            () => applicator.Apply(
                CreateScript(),
                CreateRegistry(),
                [
                    new DialogueAssignment
                    {
                        SegmentIndex = 1,
                        SpeakerId = "marcia-miller",
                        Confidence = 0.9m
                    }
                ]));
    }

    /// <summary>
    /// Verifies that unknown character IDs are rejected.
    /// </summary>
    [Fact]
    public void Apply_RejectsUnknownCharacter()
    {
        var applicator = new DialogueAttributionApplicator();

        Assert.Throws<InvalidDataException>(
            () => applicator.Apply(
                CreateScript(),
                CreateRegistry(),
                [
                    new DialogueAssignment
                    {
                        SegmentIndex = 1,
                        SpeakerId = "unknown-person",
                        Confidence = 0.9m
                    },
                    new DialogueAssignment
                    {
                        SegmentIndex = 3,
                        SpeakerId = "elias-thorne",
                        Confidence = 0.9m
                    }
                ]));
    }

    private static ChapterProductionScript CreateScript()
    {
        const string text =
            "Miller said, “Move.” Thorne answered, “Moving.”";

        var prepared = new PreparedChapter
        {
            ChapterId = "chapter-001",
            SourcePath = @"C:\Book\chapter-001.md",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            SpokenText = text
        };

        return new ProductionScriptSegmenter().Segment(
            prepared);
    }

    private static CharacterRegistry CreateRegistry()
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "test-book",
            Characters =
            [
                CreateCharacter(
                    "marcia-miller",
                    "Marcia Miller"),
                CreateCharacter(
                    "elias-thorne",
                    "Elias Thorne")
            ],
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };
    }

    private static CharacterProfile CreateCharacter(
        string id,
        string displayName)
    {
        return new CharacterProfile
        {
            Id = id,
            DisplayName = displayName,
            Aliases = [],
            Description = "",
            VoiceTraits = [],
            Importance = CharacterImportance.Major,
            IsNarrator = false
        };
    }
}
