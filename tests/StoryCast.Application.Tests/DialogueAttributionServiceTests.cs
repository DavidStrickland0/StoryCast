using System.Text.Json;
using StoryCast.Application.Attribution;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests schema-constrained dialogue attribution.
/// </summary>
public sealed class DialogueAttributionServiceTests
{
    /// <summary>
    /// Verifies generated assignments and dynamic schema restrictions.
    /// </summary>
    [Fact]
    public async Task AttributeAsync_ReturnsConstrainedAssignments()
    {
        var generator = new StubStructuredTextGenerator(
            """
            {
              "assignments": [
                {
                  "segmentIndex": 1,
                  "speakerId": "marcia-miller",
                  "confidence": 0.98,
                  "delivery": "firm",
                  "rationale": "Miller is identified by the dialogue tag."
                },
                {
                  "segmentIndex": 3,
                  "speakerId": "elias-thorne",
                  "confidence": 0.91,
                  "delivery": "controlled",
                  "rationale": "Thorne is identified by the response tag."
                }
              ]
            }
            """);

        var service = new DialogueAttributionService(generator);

        var assignments = await service.AttributeAsync(
            CreateScript(),
            CreateRegistry());

        Assert.Equal(2, assignments.Count);
        Assert.Equal(
            "marcia-miller",
            assignments[0].SpeakerId);
        Assert.Equal(
            "elias-thorne",
            assignments[1].SpeakerId);

        var assignmentSchema = generator.Schema
            .GetProperty("properties")
            .GetProperty("assignments");

        Assert.Equal(
            2,
            assignmentSchema.GetProperty("minItems").GetInt32());

        var itemProperties = assignmentSchema
            .GetProperty("items")
            .GetProperty("properties");

        var allowedIndexes = itemProperties
            .GetProperty("segmentIndex")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();

        Assert.Equal([1, 3], allowedIndexes);

        var allowedSpeakers = itemProperties
            .GetProperty("speakerId")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("marcia-miller", allowedSpeakers);
        Assert.Contains("elias-thorne", allowedSpeakers);
    }

    /// <summary>
    /// Verifies that chapters without dialogue do not invoke generation.
    /// </summary>
    [Fact]
    public async Task AttributeAsync_SkipsChapterWithoutDialogue()
    {
        var generator = new StubStructuredTextGenerator(
            """{"assignments":[]}""");

        var script = new ChapterProductionScript
        {
            ChapterId = "chapter-001",
            SourceText = "Only narration.",
            Segments =
            [
                new ProductionSegment
                {
                    Index = 0,
                    SourceStart = 0,
                    SourceLength = 15,
                    SourceText = "Only narration.",
                    SpeakerId = "narrator",
                    Kind = SegmentKind.Narration
                }
            ]
        };

        var service = new DialogueAttributionService(generator);

        var assignments = await service.AttributeAsync(
            script,
            CreateRegistry());

        Assert.Empty(assignments);
        Assert.Equal(0, generator.CallCount);
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

    private sealed class StubStructuredTextGenerator
        : IStructuredTextGenerator
    {
        private readonly string response;

        public StubStructuredTextGenerator(string response)
        {
            this.response = response;
        }

        public int CallCount { get; private set; }

        public JsonElement Schema { get; private set; }

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            JsonElement jsonSchema,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Schema = jsonSchema.Clone();

            return Task.FromResult(response);
        }
    }
}
