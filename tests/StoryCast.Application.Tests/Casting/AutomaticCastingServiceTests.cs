using System.Text.Json;
using StoryCast.Application.Casting;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Tests.Casting;

/// <summary>
/// Tests schema-constrained automatic casting.
/// </summary>
public sealed class AutomaticCastingServiceTests
{
    /// <summary>
    /// Keeps the actual validation failure visible to command-line callers.
    /// </summary>
    [Theory]
    [InlineData("{", "invalid JSON")]
    [InlineData("{\"assignments\":[]}", "exactly the requested roles")]
    public async Task AssignAsync_RejectedAttempts_ReportsLastFailure(
        string response,
        string expectedFailure)
    {
        var generator = new SequenceStructuredTextGenerator(response, response, response);
        var service = new AutomaticCastingService(generator, new CastingAssignmentValidator());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AssignAsync(CreateRegistry("hero"), CreateVoices("voice-1", "voice-2"),
                [new CastingAssignment { CharacterId = "narrator", VoiceId = "voice-2", Confidence = 1, Rationale = "Existing" }]));

        Assert.Equal(3, generator.CallCount);
        Assert.Contains(expectedFailure, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(exception.InnerException);
        Assert.Contains(exception.InnerException.Message, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFailure, generator.UserPrompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.All(generator.Schemas, schema => Assert.All(
            schema.GetProperty("properties").GetProperty("assignments")
                .GetProperty("items").GetProperty("anyOf").EnumerateArray(),
            roleSchema => Assert.Equal(
                500,
                roleSchema.GetProperty("properties").GetProperty("rationale")
                    .GetProperty("maxLength").GetInt32())));
    }

    /// <summary>
    /// Verifies that duplicate roles from the model are rejected and retried.
    /// </summary>
    [Fact]
    public async Task AssignAsync_DuplicateRole_Retries()
    {
        var generator = new SequenceStructuredTextGenerator(
            """
            {
              "assignments": [
                {
                  "characterId": "dejah-thoris",
                  "voiceId": "voice-1",
                  "confidence": 0.9,
                  "rationale": "First duplicate."
                },
                {
                  "characterId": "dejah-thoris",
                  "voiceId": "voice-2",
                  "confidence": 0.8,
                  "rationale": "Second duplicate."
                },
                {
                  "characterId": "narrator",
                  "voiceId": "voice-3",
                  "confidence": 0.9,
                  "rationale": "Narrator."
                }
              ]
            }
            """,
            """
            {
              "assignments": [
                {
                  "characterId": "dejah-thoris",
                  "voiceId": "voice-1",
                  "confidence": 0.9,
                  "rationale": "Dejah Thoris."
                },
                {
                  "characterId": "john-carter",
                  "voiceId": "voice-2",
                  "confidence": 0.9,
                  "rationale": "John Carter."
                },
                {
                  "characterId": "narrator",
                  "voiceId": "voice-3",
                  "confidence": 0.9,
                  "rationale": "Narrator."
                }
              ]
            }
            """);

        var service = new AutomaticCastingService(
            generator,
            new CastingAssignmentValidator());

        var assignments = await service.AssignAsync(
            CreateRegistry(
                "dejah-thoris",
                "john-carter"),
            CreateVoices(
                "voice-1",
                "voice-2",
                "voice-3"),
            []);

        Assert.Equal(2, generator.CallCount);
        Assert.Equal(3, assignments.Count);

        Assert.Contains(
            assignments,
            assignment =>
                assignment.CharacterId == "john-carter");

        Assert.Contains(
            "previous casting response was invalid",
            generator.UserPrompts[1],
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that existing roles and their voices are excluded from
    /// incremental automatic casting.
    /// </summary>
    [Fact]
    public async Task AssignAsync_ExistingAssignments_CastsOnlyNewRole()
    {
        var generator = new SequenceStructuredTextGenerator(
            """
            {
              "assignments": [
                {
                  "characterId": "new-character",
                  "voiceId": "voice-3",
                  "confidence": 0.9,
                  "rationale": "Unused voice for the new character."
                }
              ]
            }
            """);

        var service = new AutomaticCastingService(
            generator,
            new CastingAssignmentValidator());

        var existingAssignments =
            new[]
            {
                CreateAssignment(
                    "existing-character",
                    "voice-1"),
                CreateAssignment(
                    "narrator",
                    "voice-2")
            };

        var assignments = await service.AssignAsync(
            CreateRegistry(
                "existing-character",
                "new-character"),
            CreateVoices(
                "voice-1",
                "voice-2",
                "voice-3"),
            existingAssignments);

        var assignment = Assert.Single(assignments);

        Assert.Equal(
            "new-character",
            assignment.CharacterId);

        Assert.Equal(
            "voice-3",
            assignment.VoiceId);

        Assert.DoesNotContain(
            "voice-1.wav",
            generator.UserPrompts[0],
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "voice-2.wav",
            generator.UserPrompts[0],
            StringComparison.Ordinal);
    }

    private static CharacterRegistry CreateRegistry(
        params string[] characterIds)
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "book-1",
            Characters = characterIds
                .Select(
                    characterId => new CharacterProfile
                    {
                        Id = characterId,
                        DisplayName = characterId
                    })
                .ToArray(),
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };
    }

    private static IReadOnlyList<VoiceProfile> CreateVoices(
        params string[] voiceIds)
    {
        return voiceIds
            .Select(
                voiceId => new VoiceProfile
                {
                    Id = voiceId,
                    SamplePath = $"{voiceId}.wav"
                })
            .ToArray();
    }

    private static CastingAssignment CreateAssignment(
        string characterId,
        string voiceId)
    {
        return new CastingAssignment
        {
            CharacterId = characterId,
            VoiceId = voiceId,
            Confidence = 0.9m,
            Rationale = "Existing assignment."
        };
    }

    private sealed class SequenceStructuredTextGenerator
        : IStructuredTextGenerator
    {
        private readonly Queue<string> responses;

        public SequenceStructuredTextGenerator(
            params string[] responses)
        {
            this.responses = new Queue<string>(
                responses);
        }

        public int CallCount { get; private set; }

        public List<string> UserPrompts { get; } = [];

        public List<JsonElement> Schemas { get; } = [];

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            JsonElement jsonSchema,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserPrompts.Add(userPrompt);
            Schemas.Add(jsonSchema.Clone());

            if (responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No generated response remains.");
            }

            return Task.FromResult(
                responses.Dequeue());
        }
    }
}
