using System.Text.Json;
using StoryCast.Application.Casting;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Tests.Casting;

/// <summary>Tests role-specific casting eligibility and recovery.</summary>
public sealed class AutomaticCastingEligibilityTests
{
    /// <summary>Excludes incompatible voices in the schema and retries invalid model output.</summary>
    [Fact]
    public async Task AssignAsync_FemaleRole_ConstrainsVoicesAndRecovers()
    {
        var generator = new Generator(
            Response(("receptionist", "male"), ("narrator", "female")),
            Response(("receptionist", "female"), ("narrator", "male")));
        var result = await Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []);
        Assert.Equal(2, generator.Calls);
        Assert.Equal("female", result.Single(item => item.CharacterId == "receptionist").VoiceId);
        Assert.Equal(new[] { "female" }, AllowedVoices(generator.Schema, "receptionist"));
        Assert.Equal(new[] { "female", "male" }, AllowedVoices(generator.Schema, "narrator"));
    }

    /// <summary>Reserves existing voices and casts only the new role.</summary>
    [Fact]
    public async Task AssignAsync_ExistingNarrator_PreservesAssignmentAndExcludesReservedVoice()
    {
        var generator = new Generator(Response(("receptionist", "female")));
        var existing = new CastingAssignment
        {
            CharacterId = "narrator", VoiceId = "reserved", Confidence = 1, Rationale = "Locked", IsLocked = true
        };
        var result = await Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("reserved", "female"), Voice("female", "female")], [existing]);
        Assert.Single(result);
        Assert.Equal(new[] { "female" }, AllowedVoices(generator.Schema, "receptionist"));
        Assert.True(existing.IsLocked);
        Assert.Equal("reserved", existing.VoiceId);
    }

    /// <summary>Reports missing compatible samples before any model call.</summary>
    [Fact]
    public async Task AssignAsync_NoFemaleVoice_FailsBeforeGeneration()
    {
        var generator = new Generator();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("male1", "male"), Voice("male2", "male")], []));
        Assert.Equal(0, generator.Calls);
        Assert.Contains("no unused verified voices", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Detects a shared compatible-voice shortage before model generation.</summary>
    [Fact]
    public async Task AssignAsync_NotEnoughDistinctFemaleVoices_FailsBeforeGeneration()
    {
        var generator = new Generator();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(generator).AssignAsync(
            Registry("receptionist", "thorne"),
            [Voice("female", "female"), Voice("male1", "male"), Voice("male2", "male")], []));
        Assert.Equal(0, generator.Calls);
        Assert.Contains("only 1 compatible", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Keeps exhausted failures bounded with the final validation reason.</summary>
    [Fact]
    public async Task AssignAsync_IncompatibleResponse_StopsAfterThree()
    {
        var invalid = Response(("receptionist", "male"), ("narrator", "female"));
        var generator = new Generator(invalid, invalid, invalid);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []));
        Assert.Equal(3, generator.Calls);
        Assert.Contains("after 3 attempts", error.Message, StringComparison.Ordinal);
        Assert.Contains("allowed voice IDs", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Does not perform model calls after cancellation.</summary>
    [Fact]
    public async Task AssignAsync_Canceled_DoesNotGenerate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var generator = new Generator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], [], cancellation.Token));
        Assert.Equal(0, generator.Calls);
    }

    private static AutomaticCastingService Service(Generator generator) => new(generator, new CastingAssignmentValidator());

    /// <summary>Malformed model output is retried instead of escaping validation.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"assignments\":null}")]
    [InlineData("{\"assignments\":[null]}")]
    [InlineData("{\"assignments\":[{\"characterId\":null,\"voiceId\":\"female\",\"rationale\":\"Suitable\"},{\"characterId\":\"narrator\",\"voiceId\":\"male\",\"rationale\":\"Suitable\"}]}")]
    [InlineData("{\"assignments\":[{\"characterId\":\"receptionist\",\"voiceId\":null,\"rationale\":\"Suitable\"}]}")]
    [InlineData("{\"assignments\":[{\"characterId\":\"receptionist\",\"voiceId\":\"female\",\"rationale\":null}]}")]
    public async Task AssignAsync_MalformedResponse_Retries(string invalid)
    {
        var generator = new Generator(invalid,
            Response(("receptionist", "female"), ("narrator", "male")));

        var result = await Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []);

        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, result.Count);
    }

    /// <summary>Generator validation failures use the same bounded recovery path.</summary>
    [Fact]
    public async Task AssignAsync_GeneratorReturnsEmptyStructuredResponse_Retries()
    {
        var generator = new Generator(
            Response(("receptionist", "female"), ("narrator", "male")))
        {
            InitialFailure = new InvalidDataException("Ollama returned an empty structured response.")
        };

        var result = await Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []);

        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, result.Count);
    }

    /// <summary>Transport failures propagate without redundant casting retries.</summary>
    [Fact]
    public async Task AssignAsync_TransportFailure_DoesNotRetry()
    {
        var generator = new Generator { InitialFailure = new HttpRequestException("Unavailable") };

        await Assert.ThrowsAsync<HttpRequestException>(() => Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []));

        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Compatible voices must still be unique across roles.</summary>
    [Fact]
    public async Task AssignAsync_DuplicateVoice_Retries()
    {
        var generator = new Generator(
            Response(("receptionist", "female"), ("narrator", "female")),
            Response(("receptionist", "female"), ("narrator", "male")));

        var result = await Service(generator).AssignAsync(
            Registry("receptionist"), [Voice("female", "female"), Voice("male", "male")], []);

        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, result.Select(item => item.VoiceId).Distinct().Count());
    }

    /// <summary>Individual casting rejects invalid confidence and retains unique compatible voices.</summary>
    [Fact]
    public async Task AssignAsync_FourRoles_RetriesInvalidIndividualConfidence()
    {
        static string Selection(string voice, decimal confidence = 0.9m) =>
            JsonSerializer.Serialize(new { voiceId = voice, confidence, rationale = "Suitable" });

        var generator = new Generator(
            Selection("female1", 2m), Selection("female1"),
            Selection("female2"), Selection("female3"), Selection("male"));
        var result = await Service(generator).AssignAsync(
            Registry("a", "b", "c"),
            [Voice("female1", "female"), Voice("female2", "female"),
             Voice("female3", "female"), Voice("male", "male")], []);

        Assert.Equal(5, generator.Calls);
        Assert.Equal(4, result.Count);
        Assert.Equal(4, result.Select(item => item.VoiceId).Distinct().Count());
        Assert.All(result, assignment => Assert.InRange(assignment.Confidence, 0m, 1m));
        Assert.Equal("male", result.Single(item => item.CharacterId == "narrator").VoiceId);
    }

    private static CharacterRegistry Registry(params string[] ids) => new()
    {
        SchemaVersion = 1,
        BookId = "book",
        Characters = ids.Select(id => new CharacterProfile { Id = id, DisplayName = id, VoicePresentation = "female" }).ToArray(),
        ProcessedChapterHashes = new Dictionary<string, string>()
    };

    private static VoiceProfile Voice(string id, string presentation) => new()
    {
        Id = id, SamplePath = id + ".wav", Presentation = presentation
    };

    private static string Response(params (string Role, string Voice)[] pairs) => JsonSerializer.Serialize(new
    {
        assignments = pairs.Select(pair => new { characterId = pair.Role, voiceId = pair.Voice, confidence = 0.9, rationale = "Suitable" })
    });

    private static string[] AllowedVoices(JsonElement schema, string role) => schema.GetProperty("properties")
        .GetProperty("assignments").GetProperty("items").GetProperty("anyOf").EnumerateArray()
        .Select(item => item.GetProperty("properties"))
        .Single(item => item.GetProperty("characterId").GetProperty("enum")[0].GetString() == role)
        .GetProperty("voiceId").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private sealed class Generator(params string[] responses) : IStructuredTextGenerator
    {
        public int Calls { get; private set; }
        public JsonElement Schema { get; private set; }
        public Exception? InitialFailure { get; init; }

        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, JsonElement jsonSchema, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Schema = jsonSchema.Clone();
            Calls++;
            if (Calls == 1 && InitialFailure is not null)
            {
                throw InitialFailure;
            }

            return Task.FromResult(responses[Calls - 1 - (InitialFailure is null ? 0 : 1)]);
        }
    }
}
