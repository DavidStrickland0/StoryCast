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
    /// <summary>Explicit label mappings resolve duplicate names independently of chapter-local IDs.</summary>
    [Fact]
    public async Task AttributeAsync_LabelMapping_SelectsCanonicalIdentity()
    {
        var registry = new CharacterRegistry
        {
            SchemaVersion = 1, BookId = "test-book",
            Characters = [CreateCharacter("tim", "Tim"), CreateCharacter("chapter-001-user", "User"), CreateCharacter("chapter-002-user", "User")],
            ProcessedChapterHashes = new Dictionary<string, string>()
        };
        DialogueConvention[] rules = [new() { Open = '[', Close = ']', SpeakerId = "tim", SpeakerLabelsOverride = true,
            SpeakerIdsByLabel = new Dictionary<string, string> { ["User"] = "chapter-001-user" } }];
        var script = new ProductionScriptSegmenter(conventions: rules).Segment(new PreparedChapter
        {
            ChapterId = "chapter-002", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = "[User (Earth): Hello.]"
        });
        var generator = new StubStructuredTextGenerator("{}");
        var result = await new DialogueAttributionService(generator, conventions: rules).AttributeAsync(script, registry);
        Assert.Equal("chapter-001-user", Assert.Single(result).SpeakerId);
        Assert.Equal(0, generator.CallCount);
    }

    /// <summary>Named messages override a book's default; system fields do not.</summary>
    [Theory]
    [InlineData("[Establishing… Establishing…]", "tim")]
    [InlineData("[Luna (Earth): He has a point.]", "luna")]
    [InlineData("[Luna: He has a point.]", "luna")]
    [InlineData("[Genetic match score: 53%]", "tim")]
    public async Task AttributeAsync_BookSpecificLabelsOverrideDefault(string text, string expected)
    {
        DialogueConvention[] rules = [new() { Open = '[', Close = ']', SpeakerId = "tim", SpeakerLabelsOverride = true }];
        var script = new ProductionScriptSegmenter(conventions: rules).Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new StubStructuredTextGenerator("{}");
        var result = await new DialogueAttributionService(generator, conventions: rules)
            .AttributeAsync(script, CreateTimAndLunaRegistry());
        Assert.Equal(expected, Assert.Single(result).SpeakerId);
        Assert.Equal(0, generator.CallCount);
    }

    /// <summary>Explicit book conventions bypass model guesses for multiple designated speakers.</summary>
    [Fact]
    public async Task AttributeAsync_DelimiterRules_AssignSpeakersWithoutGeneration()
    {
        DialogueConvention[] rules =
        [
            new() { Open = '[', Close = ']', SpeakerId = "tim" },
            new() { Open = '{', Close = '}', SpeakerId = "luna" }
        ];
        var script = new ProductionScriptSegmenter(conventions: rules).Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = "[Scan complete.]\n\n{Can you hear me?}"
        });
        var generator = new StubStructuredTextGenerator("{}");
        var result = await new DialogueAttributionService(generator, conventions: rules)
            .AttributeAsync(script, CreateTimAndLunaRegistry());
        Assert.Equal(0, generator.CallCount);
        Assert.Equal(new[] { "tim", "luna" }, result.Select(assignment => assignment.SpeakerId));
        Assert.All(result, assignment => Assert.Equal(1m, assignment.Confidence));
    }

    /// <summary>Only unmapped dialogue is submitted to the attribution model.</summary>
    [Fact]
    public async Task AttributeAsync_MixedDialogue_KeepsMappedSpeakerOutOfModelBatch()
    {
        DialogueConvention[] rules = [new() { Open = '[', Close = ']', SpeakerId = "tim" }];
        var script = new ProductionScriptSegmenter(conventions: rules).Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = "[Scan complete.]\n\n\"Hello.\""
        });
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new { segmentIndex = index, speakerId = "luna",
                confidence = 0.85m, delivery = "calm", rationale = "Established speaker." })
        }));
        var result = await new DialogueAttributionService(generator, conventions: rules)
            .AttributeAsync(script, CreateTimAndLunaRegistry());
        Assert.Single(Assert.Single(generator.Requests).Indexes);
        Assert.Equal(new[] { "tim", "luna" }, result.Select(assignment => assignment.SpeakerId));
    }

    /// <summary>An invalid mapping cannot silently fall back to a guessed speaker.</summary>
    [Fact]
    public async Task AttributeAsync_UnknownMappedSpeaker_RejectsConfiguration()
    {
        var generator = new StubStructuredTextGenerator("{}");
        DialogueConvention[] rules = [new() { Open = '[', Close = ']', SpeakerId = "missing" }];
        await Assert.ThrowsAsync<InvalidDataException>(() => new DialogueAttributionService(generator, conventions: rules)
            .AttributeAsync(CreateScript(), CreateTimAndLunaRegistry()));
        Assert.Equal(0, generator.CallCount);
    }

    /// <summary>Rejects a valid ID that contradicts the explicitly identified speaker.</summary>
    [Theory]
    [InlineData("Continuation of Tim's response in the same paragraph; he introduces himself as Tim.")]
    [InlineData("Narration describes Tim speaking with weariness and explains his project's progress.")]
    [InlineData("The speaker is Tim, introducing himself.")]
    [InlineData("Tim introduces himself as Tim.")]
    [InlineData("Dialogue is attributed to Tim by his introduction.")]
    [InlineData("Continuation of Tim’s explanation in the same paragraph.")]
    public async Task AttributeAsync_ContradictorySpeakerEvidence_Retries(string rationale)
    {
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-036", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = "He pushes his glasses up. \"I'm Tim.\""
        });
        var registry = CreateTimAndLunaRegistry();
        var generator = new BatchGenerator((attempt, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index, speakerId = attempt == 1 ? "luna" : "tim",
                confidence = 0.95m, delivery = "calm", rationale
            })
        }));

        var result = await new DialogueAttributionService(generator).AttributeAsync(script, registry);

        Assert.Equal("tim", Assert.Single(result).SpeakerId);
        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains("rationale identifies 'Tim'", generator.Requests[1].Prompt);
    }

    private static CharacterRegistry CreateTimAndLunaRegistry() => new()
    {
        SchemaVersion = 1, BookId = "test-book",
        Characters = [CreateCharacter("tim", "Tim"), CreateCharacter("luna", "Luna")],
        ProcessedChapterHashes = new Dictionary<string, string>()
    };

    /// <summary>A listener's name does not establish a contradiction.</summary>
    [Theory]
    [InlineData("Luna is addressing Tim directly.")]
    [InlineData("Tim is the listener, not the speaker.")]
    [InlineData("Conversational reply anchored by earlier dialogue tag 'Tim said'.")]
    public async Task AttributeAsync_ListenerMention_DoesNotReject(string rationale)
    {
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index, speakerId = "luna", confidence = 0.85m,
                delivery = "calm", rationale
            })
        }));
        var result = await new DialogueAttributionService(generator).AttributeAsync(CreateScript(), CreateTimAndLunaRegistry());
        Assert.Single(generator.Requests);
        Assert.All(result, assignment => Assert.Equal("luna", assignment.SpeakerId));
    }

    /// <summary>Shared aliases cannot be used to select one registry identity.</summary>
    [Fact]
    public async Task AttributeAsync_AmbiguousSpeakerName_DoesNotReject()
    {
        var registry = new CharacterRegistry
        {
            SchemaVersion = 1, BookId = "test-book",
            Characters = [CreateCharacter("tim-01", "Tim"), CreateCharacter("tim-02", "Tim")],
            ProcessedChapterHashes = new Dictionary<string, string>()
        };
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index, speakerId = "tim-01", confidence = 0.75m,
                delivery = "calm", rationale = "The speaker is Tim, identified by context."
            })
        }));
        await new DialogueAttributionService(generator).AttributeAsync(CreateScript(), registry);
        Assert.Single(generator.Requests);
    }

    /// <summary>Persistent contradictions cannot pass as confident assignments.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttributeAsync_PersistentContradiction_FailsOrMarksForReview(bool allowReview)
    {
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index, speakerId = "luna", confidence = 0.98m,
                delivery = "calm", rationale = "Continuation of Tim's response."
            })
        }));
        var service = new DialogueAttributionService(generator, allowReview);
        if (allowReview)
        {
            var result = await service.AttributeAsync(CreateScript(), CreateTimAndLunaRegistry());
            Assert.All(result, assignment => Assert.Equal(0m, assignment.Confidence));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.AttributeAsync(CreateScript(), CreateTimAndLunaRegistry()));
            Assert.Contains("rationale identifies 'Tim'", failure.Message);
        }

        Assert.Equal(3, generator.Requests.Count);
    }

    /// <summary>Exhausted invalid responses produce provisional, explicitly uncertain audio assignments only when enabled.</summary>
    [Fact]
    public async Task AttributeAsync_ReviewModeFallsBackAfterBoundedInvalidResponses()
    {
        var generator = new StubStructuredTextGenerator("{}");
        var result = await new DialogueAttributionService(generator, allowReview: true).AttributeAsync(CreateScript(), CreateRegistry());
        Assert.Equal(3, generator.CallCount);
        Assert.Equal(2, result.Count);
        Assert.All(result, assignment =>
        {
            Assert.Equal(0m, assignment.Confidence);
            Assert.Contains("review", assignment.Rationale);
        });
    }

    /// <summary>Accepts abbreviated evidence that occurs in the turn's own paragraph.</summary>
    [Theory]
    [InlineData("...")]
    [InlineData("\u2026")]
    public async Task AttributeAsync_AllowsAbbreviatedCitationInOwnParagraph(string ellipsis)
    {
        const string text = "Miller said, \"Oh great, another delay.\"";
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new { segmentIndex = index, speakerId = "marcia-miller",
                confidence = 0.95m, delivery = "dry", rationale = $"Direct dialogue tag 'Oh great{ellipsis}' in this paragraph." })
        }));
        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());
        Assert.Single(generator.Requests);
        Assert.Equal("marcia-miller", Assert.Single(result).SpeakerId);
    }

    /// <summary>Allows an earlier tag to anchor a contextual reply without claiming direct proof.</summary>
    [Fact]
    public async Task AttributeAsync_AllowsContextualReferenceToEarlierTag()
    {
        const string text = "Marcia Miller said, \"Move.\"\n\n\"Why?\"";
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index,
                speakerId = index == indexes[0] ? "marcia-miller" : "elias-thorne",
                confidence = 0.85m, delivery = "calm",
                rationale = "Conversational reply anchored by earlier dialogue tag 'Marcia Miller said'."
            })
        }));
        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());
        Assert.Single(generator.Requests);
        Assert.Equal("elias-thorne", result[1].SpeakerId);
    }

    /// <summary>Rejects a reply attributed using a tag from the preceding paragraph.</summary>
    [Fact]
    public async Task AttributeAsync_RejectsBorrowedDialogueTagAndRetries()
    {
        const string text = "Marcia Miller said, \"Move.\"\n\n\"Why?\"";
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((attempt, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index,
                speakerId = attempt == 1 || index == indexes[0] ? "marcia-miller" : "elias-thorne",
                confidence = 0.95m, delivery = "calm",
                rationale = attempt == 1 ? "Directly identified by dialogue tag 'Marcia Miller said'." : "Established conversational reply."
            })
        }));
        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());
        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains("outside its source paragraph", generator.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal("elias-thorne", result[1].SpeakerId);
    }

    /// <summary>Preserves tag scope when an untagged reply starts a new paragraph.</summary>
    [Fact]
    public async Task AttributeAsync_ProvidesParagraphScopedTurnEvidence()
    {
        const string text = "\"Would you turn away?\" Marcia Miller asked.\n\n\"Why?\"\n\n\"Because I am changing.\"";
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => CompleteResponse(indexes));
        await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());
        var prompt = Assert.Single(generator.Requests).Prompt;
        var paragraphs = prompt.Split("here belong to their own paragraph, not the next turn):")[1]
            .Split("Physical source lines")[0].Trim();
        using var document = JsonDocument.Parse(paragraphs);
        var turns = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, turns.Length);
        Assert.Equal("\"Would you turn away?\" Marcia Miller asked.", turns[0].GetProperty("Paragraph").GetString());
        Assert.Equal("\"Why?\"", turns[1].GetProperty("Paragraph").GetString());
        Assert.Equal("\"Because I am changing.\"", turns[2].GetProperty("Paragraph").GetString());
    }

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

    /// <summary>Verifies bounded batches retain full chapter context and global indexes.</summary>
    [Fact]
    public async Task AttributeAsync_LargeChapter_BatchesWithoutLosingContext()
    {
        var script = CreateLongScript();
        var generator = new BatchGenerator((_, indexes, _) => CompleteResponse(indexes));
        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());
        Assert.Equal(3, generator.Requests.Count);
        Assert.Equal(new[] { 8, 8, 3 }, generator.Requests.Select(request => request.Indexes.Length));
        Assert.Equal(script.Segments.Where(segment => segment.Kind == SegmentKind.Dialogue).Select(segment => segment.Index),
            result.Select(assignment => assignment.SegmentIndex));
        Assert.All(generator.Requests, request =>
        {
            Assert.Contains("Line 0", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Line 18", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Assign ONLY these dialogue indexes", request.Prompt, StringComparison.Ordinal);
        });
    }

    /// <summary>Later batches use source context without copying earlier model guesses.</summary>
    [Fact]
    public async Task AttributeAsync_UntaggedExchange_DoesNotFeedBackSpeakerGuesses()
    {
        var text = "Miller faced Thorne.\n\n" + string.Join("\n\n",
            Enumerable.Range(0, 10).Select(index => $"\"Turn {index}.\""));
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001",
            SourcePath = @"C:\Book\exchange.md",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => JsonSerializer.Serialize(new
        {
            assignments = indexes.Select(index => new
            {
                segmentIndex = index,
                speakerId = index % 2 == 1 ? "marcia-miller" : "elias-thorne",
                confidence = 0.85m,
                delivery = "calm",
                rationale = "Established two-person exchange."
            })
        }));

        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());

        Assert.Equal(10, result.Count);
        Assert.Equal(2, generator.Requests.Count);
        Assert.All(generator.Requests, request =>
        {
            Assert.Contains("Miller faced Thorne.", request.Prompt);
            Assert.Contains("Turn 0.", request.Prompt);
            Assert.Contains("Turn 9.", request.Prompt);
            Assert.DoesNotContain("Earlier batch assignments", request.Prompt);
            Assert.DoesNotContain("Established two-person exchange.", request.Prompt);
        });
    }

    /// <summary>Single newlines remain visible as distinct reply boundaries.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task AttributeAsync_SingleNewlineExchange_ProvidesDistinctSourceLines(string newline)
    {
        string[] lines = ["I said \"Hello\"", "\"Hello\"", "\"How are You\"", "\"I am fine\""];
        var text = string.Join(newline, lines);
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-001", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => CompleteResponse(indexes));
        await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());

        var prompt = Assert.Single(generator.Requests).Prompt;
        var context = prompt.Split("turn boundaries even when several turns share one paragraph):")[1]
            .Split("Return exactly")[0].Trim();
        using var document = JsonDocument.Parse(context);
        var turns = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(4, turns.Length);
        Assert.Equal(lines, turns.Select(turn => turn.GetProperty("Line").GetString()));
        Assert.False(turns[0].GetProperty("BeginsQuotedLine").GetBoolean());
        Assert.All(turns.Skip(1), turn => Assert.True(turn.GetProperty("BeginsQuotedLine").GetBoolean()));
        Assert.Equal(text, string.Concat(script.Segments.Select(segment => segment.SourceText)));
    }

    /// <summary>Introductions and continued speeches are kept together at batch boundaries.</summary>
    [Fact]
    public async Task AttributeAsync_ParagraphWouldCrossBatchBoundary_KeepsQuotesTogether()
    {
        var text = string.Join("\n\n", Enumerable.Repeat("\"Earlier reply.\"", 7)) +
            "\n\nHe looks up. \"Hello.\" He adjusts his glasses. \"I'm Tim.\" He nods. \"Nice to meet you.\"";
        var script = new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-036", SourcePath = "chapter.md", SourceSha256 = new string('a', 64),
            PreparationVersion = "1", SpokenText = text
        });
        var generator = new BatchGenerator((_, indexes, _) => CompleteResponse(indexes));
        var result = await new DialogueAttributionService(generator).AttributeAsync(script, CreateRegistry());

        Assert.Equal(new[] { 7, 3 }, generator.Requests.Select(request => request.Indexes.Length));
        Assert.Equal(script.Segments.Where(segment => segment.Kind == SegmentKind.Dialogue).Select(segment => segment.Index),
            result.Select(assignment => assignment.SegmentIndex));
        var introductionIndexes = script.Segments.Where(segment => segment.Kind == SegmentKind.Dialogue)
            .Skip(7).Select(segment => segment.Index).ToArray();
        Assert.Equal(introductionIndexes, generator.Requests[1].Indexes);
    }

    /// <summary>Verifies only an invalid batch is retried, with complete coverage.</summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("null")]
    public async Task AttributeAsync_InvalidBatch_RetriesOnlyThatBatch(string failure)
    {
        var generator = new BatchGenerator((call, indexes, _) => call != 2 ? CompleteResponse(indexes) : failure switch
        {
            "truncated" => "{\"assignments\":[",
            "duplicate" => CompleteResponse(Enumerable.Repeat(indexes[0], indexes.Length).ToArray()),
            "missing" => CompleteResponse(indexes[..^1]),
            "unexpected" => CompleteResponse(indexes.Select(index => index + 1000).ToArray()),
            _ => "{\"assignments\":null}"
        });
        var result = await new DialogueAttributionService(generator).AttributeAsync(CreateLongScript(), CreateRegistry());
        Assert.Equal(19, result.Count);
        Assert.Equal(4, generator.Requests.Count);
        Assert.Equal(generator.Requests[1].Indexes, generator.Requests[2].Indexes);
        Assert.Contains("previous response was rejected", generator.Requests[2].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("previous response was rejected", generator.Requests[3].Prompt, StringComparison.Ordinal);
    }

    /// <summary>Verifies exhaustion stops later batches and reports the failed boundary.</summary>
    [Fact]
    public async Task AttributeAsync_InvalidBatchExhausted_StopsWithoutPartialResult()
    {
        var generator = new BatchGenerator((call, indexes, _) => call == 1 ? CompleteResponse(indexes) : "{");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DialogueAttributionService(generator).AttributeAsync(CreateLongScript(), CreateRegistry()));
        Assert.Equal(4, generator.Requests.Count);
        Assert.Contains("after 3 attempts", failure.Message, StringComparison.Ordinal);
        Assert.Contains("batch 2 of 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("indexes", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies cancellation does not trigger another attempt.</summary>
    [Fact]
    public async Task AttributeAsync_CanceledBetweenBatches_Stops()
    {
        using var cancellation = new CancellationTokenSource();
        var generator = new BatchGenerator((call, indexes, token) =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return CompleteResponse(indexes);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DialogueAttributionService(generator).AttributeAsync(CreateLongScript(), CreateRegistry(), cancellation.Token));
        Assert.Equal(2, generator.Requests.Count);
    }

    private static ChapterProductionScript CreateLongScript()
    {
        var text = string.Join(" ", Enumerable.Range(0, 19).Select(index => $"Miller said, “Line {index}.”"));
        return new ProductionScriptSegmenter().Segment(new PreparedChapter
        {
            ChapterId = "chapter-002",
            SourcePath = @"C:\Book\draft.md",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            SpokenText = text
        });
    }

    private static string CompleteResponse(int[] indexes)
    {
        return JsonSerializer.Serialize(new
        {
            assignments = indexes.Reverse().Select(index => new
            {
                segmentIndex = index,
                speakerId = "marcia-miller",
                confidence = 0.98m,
                delivery = "calm",
                rationale = "Explicit dialogue tag."
            })
        });
    }

    private sealed class BatchGenerator(Func<int, int[], CancellationToken, string> response) : IStructuredTextGenerator
    {
        public List<(int[] Indexes, string Prompt)> Requests { get; } = [];

        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, JsonElement jsonSchema, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("Do not alternate mechanically", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("A single newline is enough to separate dialogue turns", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("successive quoted paragraphs normally alternate speakers", systemPrompt, StringComparison.Ordinal);
            var array = jsonSchema.GetProperty("properties").GetProperty("assignments");
            var indexes = array.GetProperty("items").GetProperty("properties").GetProperty("segmentIndex")
                .GetProperty("enum").EnumerateArray().Select(index => index.GetInt32()).ToArray();
            Assert.Equal(indexes.Length, array.GetProperty("minItems").GetInt32());
            Assert.Equal(indexes.Length, array.GetProperty("maxItems").GetInt32());
            Requests.Add((indexes, userPrompt));
            return Task.FromResult(response(Requests.Count, indexes, cancellationToken));
        }
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
