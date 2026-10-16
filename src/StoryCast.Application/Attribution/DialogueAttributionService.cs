using System.Text.Json;
using System.Text.RegularExpressions;
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

        Untagged dialogue is often a back-and-forth exchange. Read the entire
        exchange before assigning its turns. In an established two-person
        conversation, successive quoted paragraphs normally alternate speakers
        even when there is no "he said" or "she said". Anchor the participants
        using nearby actions, questions and answers, forms of address, and later
        explicit tags. A name addressed inside a quote usually identifies the
        listener, not the speaker. Do not assign every untagged reply to the
        last explicitly tagged speaker.

        A single newline is enough to separate dialogue turns; blank lines are
        not required. Standalone quoted lines in a two-person exchange normally
        alternate speakers. For example:
        I said "Hello"
        "Hello"
        "How are You"
        "I am fine"
        Assign these turns to I, the other participant, I, the other participant.
        The first line's "I said" does not apply to subsequent quoted lines.
        Use the registry to identify the other participant; if their identity
        is unclear, lower confidence instead of assigning every line to I.
        Multiple quotations on one physical line separated by action beats
        may continue the same speaker. Wrapped prose or a clearly continuing
        monologue can override the turn pattern; a newline inside one open
        quotation is not a new speaker turn.

        A dialogue tag after a quotation identifies that preceding quotation,
        not the next paragraph's reply. Keep tags within their source paragraph.
        For example, '"Leave?" Adam asked.\n\n"Why?"' does not tag "Why?"
        as Adam. 'She asked' refers to the established female participant, even
        when the previous paragraph explicitly named a male participant.
        A speaker saying "I am an android" must be evaluated against the
        participants' identities, not copied from the previous assignment.

        Do not alternate mechanically: multiple quotes in one paragraph can
        belong to one speaker, and a continuing speech, interruption, third
        participant, or scene change can break the pattern. Use the text as
        evidence and lower confidence when the participants remain ambiguous.
        Determine each speaker afresh from the source text. A previous speaker
        is not a default for the next quote. When the narrator approaches a
        different person, someone enters or leaves, or a new conversation
        begins, reestablish the participants before assigning any replies.
        Read the narration between turns and evidence on both sides of a quote;
        a later introduction can identify an earlier untagged reply.

        Confidence must reflect the evidence:
        - 0.95 to 1.00: directly identified by a dialogue tag.
        - 0.80 to 0.94: strongly established by conversational context.
        - 0.60 to 0.79: probable but not explicit.
        - Below 0.60: materially ambiguous.

        Delivery is a free-form audible acting direction, not a fixed category.
        Use the nearby speech tags and surrounding scene to describe how THIS
        speaker delivers THIS line: for example whispering, yelling joyfully,
        screaming in fear, quietly, thoughtfully, sarcastically, through tears,
        breathlessly, or barely containing excitement. Other directions and
        combinations are equally valid. Keep it concise (at most 80 characters).
        Distinguish emotion from volume: anger can be quiet; joy can be shouted.
        A character mentioning a scream or whisper does not mean they perform
        that action. Do not invent extreme emotion unsupported by the text.
        Preserve each character's speakerId regardless of delivery; never assign
        a different speaker to obtain a performance. Directions are metadata,
        never words to be spoken.

        Quoted messages displayed by a system, textbox, or chat interface belong
        to the established interface speaker. A comparison such as "like a
        guardian angel" does not identify a mother or introduce a new speaker.

        Before returning each assignment, verify that speakerId belongs to the
        character identified as speaking in the rationale. Do not select the
        listener's ID or carry an earlier speaker into a new introduction.
        For example, a rationale saying "Continuation of Tim's response" must
        use Tim's registry ID, never Luna's. Keep quotes separated by an action
        beat in the same speech assigned to that speaker unless the text
        explicitly introduces another speaker.

        Rationale must briefly identify the evidence used. Return only JSON
        matching the supplied schema.
        """;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly IStructuredTextGenerator generator;
    private readonly bool allowReview;

    /// <summary>
    /// Initializes a dialogue-attribution service.
    /// </summary>
    /// <param name="generator">
    /// The schema-constrained text generator.
    /// </param>
    /// <param name="allowReview">Produces provisional assignments for review after invalid model responses exhaust retries.</param>
    public DialogueAttributionService(
        IStructuredTextGenerator generator,
        bool allowReview = false)
    {
        this.generator =
            generator ??
            throw new ArgumentNullException(nameof(generator));
        this.allowReview = allowReview;
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
                character.VoicePresentation,
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

        var characterIds = registry.Characters.Select(character => character.Id).ToArray();
        var paragraphs = dialogueSegments.ToDictionary(segment => segment.Index, segment => SourceParagraph(script.SourceText, segment.SourceStart));
        var batches = CreateBatches(dialogueSegments, script.SourceText);
        var assignments = new List<DialogueAssignment>(dialogueSegments.Length);
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexes = batches[batchIndex].Select(segment => segment.Index).ToArray();
            var batchPrompt = $"""
                {userPrompt}

                Assign ONLY these dialogue indexes in this batch:
                {string.Join(", ", indexes)}

                Exact source paragraphs for the requested turns (dialogue tags
                here belong to their own paragraph, not the next turn):
                {JsonSerializer.Serialize(batches[batchIndex].Select(segment => new { segment.Index, Paragraph = paragraphs[segment.Index] }), SerializerOptions)}

                Physical source lines for requested quotes (preserve single-newline
                turn boundaries even when several turns share one paragraph):
                {JsonSerializer.Serialize(batches[batchIndex].Select(segment => new
                {
                    segment.Index,
                    Line = SourceLine(script.SourceText, segment.SourceStart),
                    BeginsQuotedLine = BeginsQuotedLine(script.SourceText, segment.SourceStart)
                }), SerializerOptions)}

                Return exactly {indexes.Length} assignments. The full ordered chapter above
                is context for speaker identity and conversational continuity. Do not
                return assignments for any other segment.
                """;
            var label = $"Chapter {script.ChapterId}, batch {batchIndex + 1} of {batches.Length}";
            assignments.AddRange(await AttributeBatchAsync(
                batchPrompt, indexes, characterIds, registry.Characters, paragraphs, label, cancellationToken));
            Console.WriteLine($"Dialogue attribution: {label} complete ({assignments.Count}/{dialogueSegments.Length} lines).");
        }

        return assignments.OrderBy(assignment => assignment.SegmentIndex).ToArray();
    }

    private static ProductionSegment[][] CreateBatches(
        ProductionSegment[] dialogueSegments,
        string sourceText)
    {
        var batches = new List<ProductionSegment[]>();
        var pending = new List<ProductionSegment>();
        // Keep an introduction or a speech interrupted by action beats in one
        // request where possible. Use offsets, not paragraph text: repeated
        // identical replies in different paragraphs are distinct turns.
        foreach (var paragraph in dialogueSegments.GroupBy(segment =>
                     ParagraphStart(sourceText, segment.SourceStart)))
        {
            foreach (var chunk in paragraph.Chunk(MaximumBatchSize))
            {
                if (pending.Count + chunk.Length > MaximumBatchSize)
                {
                    batches.Add(pending.ToArray());
                    pending.Clear();
                }

                pending.AddRange(chunk);
            }
        }

        if (pending.Count > 0)
        {
            batches.Add(pending.ToArray());
        }

        return batches.ToArray();
    }

    private static int ParagraphStart(string text, int offset)
    {
        var position = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
        var previous = text.LastIndexOf("\n\n", position, StringComparison.Ordinal);
        return previous < 0 ? 0 : previous + 2;
    }

    private static string SourceLine(string text, int offset)
    {
        var previous = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        var next = text.IndexOf('\n', offset);
        return text[(previous + 1)..(next < 0 ? text.Length : next)].TrimEnd('\r');
    }

    private static bool BeginsQuotedLine(string text, int offset)
    {
        var previous = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        return string.IsNullOrWhiteSpace(text[(previous + 1)..offset]);
    }

    private static string SourceParagraph(string text, int offset)
    {
        var position = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
        var start = ParagraphStart(text, offset);
        var next = text.IndexOf("\n\n", position, StringComparison.Ordinal);
        var end = next < 0 ? text.Length : next;
        return text[start..end];
    }

    private async Task<IReadOnlyList<DialogueAssignment>> AttributeBatchAsync(
        string userPrompt,
        IReadOnlyList<int> indexes,
        IReadOnlyList<string> characterIds,
        IReadOnlyList<CharacterProfile> characters,
        IReadOnlyDictionary<int, string> paragraphs,
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
                var validated = ValidateBatch(response, indexes, characterIds);
                foreach (var assignment in validated)
                {
                    ValidateSpeakerEvidence(assignment, characters);
                    var citation = Regex.Match(assignment.Rationale, "dialogue tag\\s+['\"](?<tag>[^'\"]+)['\"]", RegexOptions.IgnoreCase);
                    var claimsDirectTag = assignment.Confidence >= 0.95m || Regex.IsMatch(assignment.Rationale, "\\b(?:directly|explicitly|explicit|direct)\\b", RegexOptions.IgnoreCase);
                    var citedText = citation.Groups["tag"].Value.TrimEnd('.', '\u2026').Trim();
                    if (citation.Success && claimsDirectTag && (citedText.Length == 0 || !paragraphs[assignment.SegmentIndex].Contains(citedText, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidDataException($"Segment {assignment.SegmentIndex} cites dialogue tag '{citation.Groups["tag"].Value}' outside its source paragraph. Reconsider the speaker using this turn's paragraph and conversational context; do not borrow a preceding turn's tag.");
                    }
                }

                return validated;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                finalFailure = exception;
                Console.WriteLine($"Dialogue attribution: {label}, attempt {attempt} rejected: {exception.Message}");
            }
        }

        if (allowReview)
        {
            var fallback = characterIds.FirstOrDefault(id => id.Equals("narrator", StringComparison.OrdinalIgnoreCase))
                ?? characterIds[0];
            return indexes.Select(index => new DialogueAssignment
            {
                SegmentIndex = index, SpeakerId = fallback, Confidence = 0m, Delivery = "",
                Rationale = "Attribution unresolved after bounded retries; provisional voice for review."
            }).ToArray();
        }

        throw new InvalidDataException(
            $"Dialogue attribution remained invalid after {MaximumAttempts} attempts " +
            $"for {label}, indexes {string.Join(", ", indexes)}. Final failure: {finalFailure?.Message}",
            finalFailure);
    }

    private static void ValidateSpeakerEvidence(
        DialogueAssignment assignment,
        IReadOnlyList<CharacterProfile> characters)
    {
        // Only inspect explicit speaker claims. Merely mentioning a listener,
        // or quoting an earlier turn's tag, is not a speaker identification.
        var names = characters.SelectMany(character => character.Aliases
                .Append(character.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new { Name = name.Trim(), character.Id }))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var group in names)
        {
            var ids = group.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length != 1 || ids[0].Equals(assignment.SpeakerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Regex.Escape(group.Key);
            var pattern = $@"(?:^|[.!?]\s+)(?:(?:Continuation of\s+){name}['’]s\s+(?:response|reply|statement|speech|explanation|dialogue)\b|(?:The\s+)?speaker is\s+{name}(?=\s|[.,;:]|$)|Dialogue (?:belongs to|is attributed to|is assigned to)\s+{name}(?=\s|[.,;:]|$)|{name}\s+(?:is identified\b|introduces (?:himself|herself|themselves)\b|says\b|speaks\b|asks\b|replies\b|answers\b)|Narration describes\s+{name}\s+speaking\b)";
            if (Regex.IsMatch(assignment.Rationale, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException(
                    $"Segment {assignment.SegmentIndex} assigns speakerId '{assignment.SpeakerId}' " +
                    $"but its rationale identifies '{group.Key}' (registry ID '{ids[0]}') as the speaker. " +
                    "Reconsider this turn and return a speakerId and rationale that agree with the source evidence.");
            }
        }
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
