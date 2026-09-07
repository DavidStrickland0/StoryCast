using System.Text.Json;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Uses schema-constrained generation to attribute dialogue speakers.
/// </summary>
public sealed class DialogueAttributionService
    : IDialogueAttributionService
{
    private const string SystemPrompt =
        """
        You assign speakers to dialogue for multi-voice audiobook production.

        Every supplied dialogue segment must be assigned exactly once.
        Use only character IDs included in the supplied character registry.
        Use dialogue tags, nearby narration, conversational order, names,
        aliases, pronouns, roles, and preceding exchanges as evidence.

        Confidence must reflect the evidence:
        - 0.95 to 1.00: directly identified by a dialogue tag.
        - 0.80 to 0.94: strongly established by conversational context.
        - 0.60 to 0.79: probable but not explicit.
        - Below 0.60: materially ambiguous.

        Delivery must be a short audible direction such as calm, strained,
        dry, angry, hesitant, or controlled. Do not invent extreme emotion
        unsupported by the text.

        Rationale must briefly identify the evidence used. Return only JSON
        matching the supplied schema.
        """;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly IStructuredTextGenerator generator;

    /// <summary>
    /// Initializes a dialogue-attribution service.
    /// </summary>
    /// <param name="generator">
    /// The schema-constrained text generator.
    /// </param>
    public DialogueAttributionService(
        IStructuredTextGenerator generator)
    {
        this.generator =
            generator ??
            throw new ArgumentNullException(nameof(generator));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DialogueAssignment>> AttributeAsync(
        ChapterProductionScript script,
        CharacterRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(registry);

        var dialogueSegments = script.Segments
            .Where(segment => segment.Kind == SegmentKind.Dialogue)
            .ToArray();

        if (dialogueSegments.Length == 0)
        {
            return [];
        }

        if (registry.Characters.Count == 0)
        {
            throw new InvalidDataException(
                $"Chapter '{script.ChapterId}' contains dialogue but the " +
                "character registry is empty.");
        }

        var characterContext = registry.Characters.Select(
            character => new
            {
                character.Id,
                character.DisplayName,
                character.Aliases,
                character.Description,
                character.VoiceTraits,
                Importance = character.Importance.ToString(),
                character.IsNarrator
            });

        var segmentContext = script.Segments.Select(
            segment => new
            {
                segment.Index,
                Kind = segment.Kind.ToString(),
                segment.SourceText
            });

        var userPrompt =
            $"""
            Character registry:
            {JsonSerializer.Serialize(
                characterContext,
                SerializerOptions)}

            Chapter:
            {script.ChapterId}

            Ordered segments:
            {JsonSerializer.Serialize(
                segmentContext,
                SerializerOptions)}
            """;

        var schema = CreateResponseSchema(
            dialogueSegments.Select(segment => segment.Index).ToArray(),
            registry.Characters.Select(character => character.Id).ToArray());

        var responseText = await generator.GenerateAsync(
            SystemPrompt,
            userPrompt,
            schema,
            cancellationToken);

        DialogueAttributionResponse response;

        try
        {
            response =
                JsonSerializer.Deserialize<DialogueAttributionResponse>(
                    responseText,
                    SerializerOptions) ??
                throw new InvalidDataException(
                    "Dialogue attribution returned an empty document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Dialogue attribution returned invalid JSON.",
                exception);
        }

        return response.Assignments.Select(
            assignment => new DialogueAssignment
            {
                SegmentIndex = assignment.SegmentIndex,
                SpeakerId = assignment.SpeakerId,
                Confidence = assignment.Confidence,
                Delivery = assignment.Delivery,
                Rationale = assignment.Rationale
            }).ToArray();
    }

    private static JsonElement CreateResponseSchema(
        IReadOnlyList<int> dialogueIndexes,
        IReadOnlyList<string> characterIds)
    {
        return JsonSerializer.SerializeToElement(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    assignments = new
                    {
                        type = "array",
                        minItems = dialogueIndexes.Count,
                        maxItems = dialogueIndexes.Count,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                segmentIndex = new
                                {
                                    type = "integer",
                                    @enum = dialogueIndexes
                                },
                                speakerId = new
                                {
                                    type = "string",
                                    @enum = characterIds
                                },
                                confidence = new
                                {
                                    type = "number",
                                    minimum = 0,
                                    maximum = 1
                                },
                                delivery = new
                                {
                                    type = "string"
                                },
                                rationale = new
                                {
                                    type = "string"
                                }
                            },
                            required = new[]
                            {
                                "segmentIndex",
                                "speakerId",
                                "confidence",
                                "delivery",
                                "rationale"
                            }
                        }
                    }
                },
                required = new[]
                {
                    "assignments"
                }
            });
    }

    private sealed class DialogueAttributionResponse
    {
        public IReadOnlyList<DialogueAssignmentResponse>
            Assignments
        { get; init; } = [];
    }

    private sealed class DialogueAssignmentResponse
    {
        public int SegmentIndex { get; init; }

        public string SpeakerId { get; init; } = string.Empty;

        public decimal Confidence { get; init; }

        public string Delivery { get; init; } = string.Empty;

        public string Rationale { get; init; } = string.Empty;
    }
}
