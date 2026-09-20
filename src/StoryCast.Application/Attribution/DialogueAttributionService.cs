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
    private const int MaximumBatchSize = 8;
    private const int MaximumAttempts = 3;

    private const string SystemPrompt =
        """
        You assign speakers to dialogue for multi-voice audiobook production.

        Assign only the requested batch indexes, each exactly once.
        Other chapter segments are context, not assignment targets.
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
        cancellationToken.ThrowIfCancellationRequested();

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

        var batches = dialogueSegments.Chunk(MaximumBatchSize).ToArray();
        var characterIds = registry.Characters.Select(character => character.Id).ToArray();
        var assignments = new List<DialogueAssignment>(dialogueSegments.Length);
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexes = batches[batchIndex].Select(segment => segment.Index).ToArray();
            var batchPrompt = $"""
                {userPrompt}

                Assign ONLY these dialogue indexes in this batch:
                {string.Join(", ", indexes)}

                Return exactly {indexes.Length} assignments. The full ordered chapter above
                is context for speaker identity and conversational continuity. Do not
                return assignments for any other segment.
                """;
            var label = $"Chapter {script.ChapterId}, batch {batchIndex + 1} of {batches.Length}";
            assignments.AddRange(await AttributeBatchAsync(
                batchPrompt, indexes, characterIds, label, cancellationToken));
            Console.WriteLine($"Dialogue attribution: {label} complete ({assignments.Count}/{dialogueSegments.Length} lines).");
        }

        return assignments.OrderBy(assignment => assignment.SegmentIndex).ToArray();
    }

    private async Task<IReadOnlyList<DialogueAssignment>> AttributeBatchAsync(
        string userPrompt,
        IReadOnlyList<int> indexes,
        IReadOnlyList<string> characterIds,
        string label,
        CancellationToken cancellationToken)
    {
        var schema = CreateResponseSchema(indexes, characterIds);
        Exception? finalFailure = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Dialogue attribution: {label}, attempt {attempt} of {MaximumAttempts}...");
            var attemptPrompt = finalFailure is null ? userPrompt : $"""
                {userPrompt}

                The previous response was rejected: {finalFailure.Message}
                Return a complete corrected JSON document for this batch only.
                Do not repeat, omit, or invent segment indexes. Keep rationales brief.
                """;
            var responseText = await generator.GenerateAsync(
                SystemPrompt, attemptPrompt, schema, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = JsonSerializer.Deserialize<DialogueAttributionResponse>(
                    responseText, SerializerOptions)
                    ?? throw new InvalidDataException("Dialogue attribution returned an empty document.");
                return ValidateBatch(response, indexes, characterIds);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                finalFailure = exception;
                Console.WriteLine($"Dialogue attribution: {label}, attempt {attempt} rejected: {exception.Message}");
            }
        }

        throw new InvalidDataException(
            $"Dialogue attribution remained invalid after {MaximumAttempts} attempts " +
            $"for {label}, indexes {string.Join(", ", indexes)}. Final failure: {finalFailure?.Message}",
            finalFailure);
    }

    private static IReadOnlyList<DialogueAssignment> ValidateBatch(
        DialogueAttributionResponse response,
        IReadOnlyList<int> expectedIndexes,
        IReadOnlyList<string> characterIds)
    {
        if (response.Assignments is null || response.Assignments.Any(assignment => assignment is null))
        {
            throw new InvalidDataException("Dialogue attribution returned null assignments.");
        }

        var returnedIndexes = response.Assignments.Select(assignment => assignment.SegmentIndex).ToArray();
        if (returnedIndexes.Length != expectedIndexes.Count ||
            returnedIndexes.Distinct().Count() != returnedIndexes.Length ||
            !returnedIndexes.ToHashSet().SetEquals(expectedIndexes))
        {
            throw new InvalidDataException(
                $"Expected each index exactly once: {string.Join(", ", expectedIndexes)}. " +
                $"Received: {string.Join(", ", returnedIndexes)}.");
        }

        foreach (var assignment in response.Assignments)
        {
            if (!characterIds.Contains(assignment.SpeakerId, StringComparer.OrdinalIgnoreCase) ||
                assignment.Confidence < 0 || assignment.Confidence > 1 ||
                assignment.Delivery is null || assignment.Rationale is null ||
                assignment.Delivery.Length > 80 || assignment.Rationale.Length > 300)
            {
                throw new InvalidDataException(
                    $"Invalid speaker, confidence, delivery, or rationale for segment {assignment.SegmentIndex}.");
            }
        }

        return response.Assignments.OrderBy(assignment => assignment.SegmentIndex)
            .Select(assignment => new DialogueAssignment
            {
                SegmentIndex = assignment.SegmentIndex,
                SpeakerId = assignment.SpeakerId,
                Confidence = assignment.Confidence,
                Delivery = assignment.Delivery.Trim(),
                Rationale = assignment.Rationale.Trim()
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
                                    type = "string",
                                    maxLength = 80
                                },
                                rationale = new
                                {
                                    type = "string",
                                    maxLength = 300
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
