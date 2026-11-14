using System.Text.Json;
using StoryCast.Application.Attribution;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests schema-constrained character discovery.
/// </summary>
public sealed class CharacterDiscoveryServiceTests
{
    /// <summary>Repeated established aliases coalesce without dropping conflicting identities.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task DiscoverAsync_CoalescesRepeatedEstablishedNamedCharacter()
    {
        var first = new { id = "jin-rou", displayName = "Jin Rou", aliases = new[] { "Rou" },
            description = "Cultivator", voiceTraits = new[] { "steady" }, voicePresentation = "unspecified",
            isNamed = true, importance = "major", isNarrator = false };
        var second = first with { displayName = "Rou", aliases = new[] { "Young Master" }, voiceTraits = new[] { "clear" }, isNarrator = true };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { first, second } }));
        var known = new CharacterProfile { Id = "jin-rou", DisplayName = "Jin Rou", Aliases = ["Rou"], IsNamed = true, IsNarrator = true };
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), [known]);
        var character = Assert.Single(result);
        Assert.Equal("jin-rou", character.Id);
        Assert.Contains("Young Master", character.Aliases);
        Assert.Contains("steady", character.VoiceTraits);
        Assert.Contains("clear", character.VoiceTraits);
        Assert.True(character.IsNarrator);
    }

    [Fact]
    public async Task DiscoverAsync_SourceEvidenceCorrectsSavedPresentationAndPersistsThroughMerge()
    {
        const string source = "Neolu lifted her hand.";
        var known = new CharacterProfile { Id = "student", DisplayName = "Neolu", VoicePresentation = "male" };
        var generator = new CallbackGenerator((system, _) => system.StartsWith("Independently verify")
            ? JsonSerializer.Serialize(new { characters = new[] { new { id = "student", presentation = "female", evidence = new[] { source } } } })
            : JsonSerializer.Serialize(new { characters = new[] { new { id = "student", displayName = "Neolu", aliases = Array.Empty<string>(),
                description = "Student", voiceTraits = Array.Empty<string>(), voicePresentation = "male", isNamed = true,
                importance = "minor", isNarrator = false } } }));
        var chapter = new PreparedChapter { ChapterId = "chapter-025", SourcePath = "source.txt",
            SourceSha256 = new string('a', 64), PreparationVersion = "1", SpokenText = source };
        var discovery = await new CharacterDiscoveryService(generator).DiscoverAsync(chapter, [known]);
        var registry = new CharacterRegistry { SchemaVersion = 1, BookId = "test", Characters = [known],
            ProcessedChapterHashes = new Dictionary<string, string>() };
        var merged = new CharacterRegistryMerger().Merge(registry, "test", chapter.ChapterId, chapter.SourceSha256, discovery);
        var profile = Assert.Single(merged.Characters);
        Assert.Equal("student", profile.Id);
        Assert.Equal("female", profile.VoicePresentation);
        Assert.Equal(source, Assert.Single(profile.VoicePresentationEvidence));
    }

    [Fact]
    public async Task DiscoverAsync_SplitsRepetitivePassagesWithoutLosingSource()
    {
        var source = string.Concat(Enumerable.Repeat("Miller said hello.\n", 200));
        var accepted = new List<string>();
        var failures = 0;
        var generator = new CallbackGenerator((_, prompt) =>
        {
            var text = prompt.Replace("\r\n", "\n").Split("<chapter>\n", StringSplitOptions.None)[1].Split("\n</chapter>", StringSplitOptions.None)[0];
            if (text.Length > 1500)
            {
                failures++;
                throw new HttpRequestException("prediction aborted, token repeat limit reached");
            }
            accepted.Add(text);
            return "{\"characters\":[]}";
        });
        var chapter = new PreparedChapter { ChapterId = "chapter-004", SourcePath = "source.txt",
            SourceSha256 = new string('a', 64), PreparationVersion = "1", SpokenText = source };
        Assert.Empty(await new CharacterDiscoveryService(generator).DiscoverAsync(chapter, []));
        Assert.Equal(source, string.Concat(accepted));
        Assert.Equal(3, failures);
        Assert.Equal(4, accepted.Count);
    }

    [Fact]
    public async Task DiscoverAsync_BoundsPassagesAndKeepsFullSourceInOrder()
    {
        var source = string.Concat(Enumerable.Repeat("Miller said hello.\n", 1000));
        var chapter = new PreparedChapter { ChapterId = "chapter-099", SourcePath = "source.txt",
            SourceSha256 = new string('a', 64), PreparationVersion = "1", SpokenText = source };
        var passages = new List<string>();
        var generator = new CallbackGenerator((_, prompt) =>
        {
            var text = prompt.Replace("\r\n", "\n").Split("<chapter>\n", StringSplitOptions.None)[1].Split("\n</chapter>", StringSplitOptions.None)[0];
            Assert.True(text.Length <= 6000);
            Assert.Contains("miller", prompt);
            Assert.DoesNotContain("Unrelated999", prompt);
            passages.Add(text);
            return "{\"characters\":[]}";
        });
        var known = Enumerable.Range(0, 1000).Select(i => new CharacterProfile { Id = $"other-{i}", DisplayName = $"Unrelated{i}" })
            .Append(new CharacterProfile { Id = "miller", DisplayName = "Miller", IsNamed = true }).ToArray();
        Assert.Empty(await new CharacterDiscoveryService(generator).DiscoverAsync(chapter, known));
        Assert.True(passages.Count > 1);
        Assert.Equal(source, string.Concat(passages));
    }

    [Fact]
    public async Task DiscoverAsync_RetriesTransportWithinBudgetAndDoesNotSwallowCancellation()
    {
        var calls = 0;
        var recovered = new CallbackGenerator((_, _) => ++calls < 3 ? throw new HttpRequestException("timeout") : "{\"characters\":[]}");
        Assert.Empty(await new CharacterDiscoveryService(recovered).DiscoverAsync(CreateChapter(), []));
        Assert.Equal(3, calls);
        calls = 0;
        var failed = new CallbackGenerator((_, _) => { calls++; throw new HttpRequestException("timeout"); });
        await Assert.ThrowsAsync<IOException>(() => new CharacterDiscoveryService(failed).DiscoverAsync(CreateChapter(), []));
        Assert.Equal(3, calls);
        var canceled = new CallbackGenerator((_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CharacterDiscoveryService(canceled).DiscoverAsync(CreateChapter(), []));
    }

    private sealed class CallbackGenerator(Func<string, string, string> action) : IStructuredTextGenerator
    {
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, JsonElement jsonSchema, CancellationToken cancellationToken = default)
            => Task.FromResult(action(systemPrompt, userPrompt));
    }

    /// <summary>Repeated explicit handles can converge on one legacy identity.</summary>
    [Fact]
    public async Task DiscoverAsync_CoalescesCanonicalChatHandleMetadata()
    {
        var first = new { id = "chapter-126-tessisdabest", displayName = "Tessisdabest", aliases = new[] { "Tess" },
            description = "Commenter", voiceTraits = new[] { "bright" }, voicePresentation = "unspecified",
            isNamed = false, importance = "minor", isNarrator = false };
        var second = first with { id = "chapter-019-tessisdabest", description = "Returning chat participant",
            aliases = new[] { "Tessi" }, voiceTraits = new[] { "clear" } };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { first, second } }));
        var chapter = new PreparedChapter { ChapterId = "chapter-126", SourcePath = "chapter.txt",
            SourceSha256 = new string('a', 64), PreparationVersion = "1", SpokenText = "[Tessisdabest (Earth): Hello.]" };
        var known = new CharacterProfile { Id = "chapter-019-tessisdabest", DisplayName = "Tessisdabest", IsNamed = false };
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(chapter, [known]);
        var character = Assert.Single(result);
        Assert.Equal(known.Id, character.Id);
        Assert.Contains("Tess", character.Aliases);
        Assert.Contains("Tessi", character.Aliases);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Explicit chat handles remain named and reuse a stable ID despite incorrect model flags.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_ExplicitChatHandleDoesNotCreateAnotherChapterIdentity(bool existing)
    {
        var candidate = new { id = "chapter-037-justiceistruth", displayName = "Justiceistruth", aliases = Array.Empty<string>(),
            description = "Chat participant", voiceTraits = Array.Empty<string>(), voicePresentation = "unspecified",
            isNamed = false, importance = "minor", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        var chapter = new PreparedChapter { ChapterId = "chapter-037", SourcePath = "chapter.txt",
            SourceSha256 = new string('a', 64), PreparationVersion = "1", SpokenText = "[Justiceistruth (Earth): Hello.]" };
        CharacterProfile[] known = existing
            ? [new() { Id = "chapter-013-justiceistruth", DisplayName = "Justiceistruth", IsNamed = false },
               new() { Id = "chapter-036-justiceistruth", DisplayName = "Justiceistruth", IsNamed = false }]
            : [];
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(chapter, known);
        var character = Assert.Single(result);
        Assert.True(character.IsNamed);
        if (existing) Assert.Equal("chapter-013-justiceistruth", character.Id);
    }

    /// <summary>The reserved production narrator cannot block discovery or replace existing casting.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task DiscoverAsync_ProductionNarrator_IsExcludedWithoutRetry()
    {
        var narrator = new { id = "narrator", displayName = "Narrator", aliases = Array.Empty<string>(),
            description = "Tells the story", voiceTraits = Array.Empty<string>(), voicePresentation = "male",
            isNamed = false, importance = "major", isNarrator = true };
        var maren = narrator with { id = "maren", displayName = "Maren", isNamed = true, isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { narrator, maren } }));
        var knownNarrator = new CharacterProfile { Id = "narrator", DisplayName = "Narrator", Description = "Tells the story", IsNarrator = true };
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), [knownNarrator]);
        Assert.Equal("maren", Assert.Single(result).Id);
        Assert.Equal(1, generator.Calls);
        Assert.DoesNotContain("Tells the story", generator.UserPrompt, StringComparison.Ordinal);
        Assert.True(knownNarrator.IsNarrator);
    }

    /// <summary>First-person story characters keep their own identities and narration role.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task DiscoverAsync_FirstPersonCharacter_PreservesIdentity()
    {
        var candidate = new { id = "elias", displayName = "Elias", aliases = Array.Empty<string>(),
            description = "Tells his own story", voiceTraits = Array.Empty<string>(), voicePresentation = "unspecified",
            isNamed = true, importance = "major", isNarrator = true };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), []);
        Assert.Equal("elias", Assert.Single(result).Id);
        Assert.True(result[0].IsNarrator);
    }

    [Theory]
    [InlineData("female", "male", "female")]
    [InlineData("male", "female", "male")]
    [InlineData("unspecified", "female", "unspecified")]
    public async Task DiscoverAsync_PreservesEstablishedPresentationButRefinesUnspecified(
        string established, string discovered, string expected)
    {
        var candidate = new { id = "maren", displayName = "Maren", aliases = Array.Empty<string>(),
            description = "Companion", voiceTraits = Array.Empty<string>(), voicePresentation = discovered,
            isNamed = true, importance = "major", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        var known = new CharacterProfile { Id = "maren", DisplayName = "Maren", IsNamed = true, VoicePresentation = established };
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), [known]);
        Assert.Equal(expected, Assert.Single(result).VoicePresentation);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>A wrong unnamed flag must not duplicate a known named person.</summary>
    [Fact]
    public async Task DiscoverAsync_ReusesKnownNamedIdentityDespiteWrongUnnamedFlag()
    {
        var candidate = new { id = "chapter-099-luna", displayName = "Luna", aliases = Array.Empty<string>(),
            description = "Student", voiceTraits = Array.Empty<string>(), voicePresentation = "female",
            isNamed = false, importance = "major", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        var known = new CharacterProfile { Id = "luna", DisplayName = "Luna", IsNamed = true, VoicePresentation = "female" };
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), [known]);
        Assert.Equal("luna", Assert.Single(result).Id);
        Assert.True(result[0].IsNamed);
    }
    /// <summary>Identical repeated speakers share one identity.</summary>
    [Fact]
    public async Task DiscoverAsync_CoalescesIdenticalDuplicates()
    {
        var candidate = new { id = "news-presenter-01", displayName = "News presenter", aliases = Array.Empty<string>(),
            description = "Presents the news.", voiceTraits = Array.Empty<string>(), voicePresentation = "unspecified",
            isNamed = false, importance = "minor", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate, candidate } }));
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), []);
        Assert.Equal("chapter-001-news-presenter-01", Assert.Single(result).Id);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Repeated named identities merge descriptive metadata without another discovery request.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_MergesCompatibleNamedDuplicates(bool alreadyKnown)
    {
        var first = new { id = "aranea", displayName = "Aranea", aliases = new[] { "Spider" },
            description = "A speaking spider.", voiceTraits = new[] { "calm" }, voicePresentation = "unspecified",
            isNamed = true, importance = "minor", isNarrator = false };
        var second = first with { displayName = "ARANEA", aliases = new[] { "Teacher" },
            description = "A patient teacher.", voiceTraits = new[] { "precise" },
            voicePresentation = "female", importance = "major" };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { first, second } }));
        CharacterProfile[] known = alreadyKnown
            ? [new CharacterProfile { Id = "aranea", DisplayName = "Aranea", IsNamed = true }]
            : [];
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), known);
        var profile = Assert.Single(result);
        Assert.Equal("aranea", profile.Id);
        Assert.Contains("Spider", profile.Aliases);
        Assert.Contains("Teacher", profile.Aliases);
        Assert.Contains("calm", profile.VoiceTraits);
        Assert.Contains("precise", profile.VoiceTraits);
        // The separate evidence verifier leaves unsupported presentation unspecified.
        Assert.Equal("unspecified", profile.VoicePresentation);
        Assert.Equal(CharacterImportance.Major, profile.Importance);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Shared generic names do not prevent discovery of a chapter-local copy.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_AmbiguousUnnamedRoleRemainsChapterScoped(bool isNamed)
    {
        var candidate = new { id = "simulacrum-03", displayName = "Simulacrum", aliases = Array.Empty<string>(),
            description = "A speaking copy.", voiceTraits = Array.Empty<string>(), voicePresentation = "unspecified",
            isNamed, importance = "minor", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        CharacterProfile[] known = [
            new() { Id = "copy-one", DisplayName = "Simulacrum", IsNamed = true },
            new() { Id = "copy-two", DisplayName = "Simulacrum", IsNamed = true }];
        if (isNamed)
        {
            var reused = Assert.Single(await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), known));
            Assert.Equal("copy-one", reused.Id);
            Assert.True(reused.IsNamed);
            Assert.Equal(1, generator.Calls);
            return;
        }
        var profile = Assert.Single(await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), known));
        Assert.Equal("chapter-001-simulacrum-03", profile.Id);
        Assert.False(profile.IsNamed);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>An exact display name wins over another character's shared alias.</summary>
    [Fact]
    public async Task DiscoverAsync_PrefersUniqueExactNameOverSharedAlias()
    {
        var candidate = new { id = "new-copy", displayName = "Simulacrum", aliases = Array.Empty<string>(),
            description = "A speaking copy.", voiceTraits = Array.Empty<string>(), voicePresentation = "unspecified",
            isNamed = false, importance = "minor", isNarrator = false };
        var generator = new StubStructuredTextGenerator(JsonSerializer.Serialize(new { characters = new[] { candidate } }));
        CharacterProfile[] known = [
            new() { Id = "copy-one", DisplayName = "Simulacrum", IsNamed = true },
            new() { Id = "copy-four", DisplayName = "Simulacrum number four", Aliases = ["Simulacrum"], IsNamed = true }];
        var profile = Assert.Single(await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), known));
        Assert.Equal("copy-one", profile.Id);
        Assert.True(profile.IsNamed);
    }

    /// <summary>Conflicting duplicates receive a corrective request.</summary>
    [Fact]
    public async Task DiscoverAsync_CorrectsConflictingDuplicateIds()
    {
        var first = new { id = "news-presenter-01", displayName = "News presenter", aliases = Array.Empty<string>(),
            description = "Presents the news.", voiceTraits = Array.Empty<string>(), voicePresentation = "male",
            isNamed = false, importance = "minor", isNarrator = false };
        var second = first with { voicePresentation = "female" };
        var generator = new StubStructuredTextGenerator(
            JsonSerializer.Serialize(new { characters = new[] { first, second } }),
            JsonSerializer.Serialize(new { characters = new[] { first, second with { id = "news-presenter-02" } } }));
        var result = await new CharacterDiscoveryService(generator).DiscoverAsync(CreateChapter(), []);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, generator.Calls);
        Assert.Contains("duplicate ID", generator.UserPrompt);
        Assert.NotEqual(result[0].Id, result[1].Id);
    }

    /// <summary>
    /// Verifies that valid generated characters are mapped into profiles.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_MapsValidCharacters()
    {
        var generator = new StubStructuredTextGenerator(
            """
            {
              "characters": [
                {
                  "id": "marcia-miller",
                  "isNamed": true,
                  "displayName": "Marcia Miller",
                  "aliases": [
                    "Miller",
                    "Corporal Miller"
                  ],
                  "description": "A Marine corporal.",
                  "voiceTraits": [
                    "confident",
                    "controlled"
                  ],
                  "importance": "major",
                  "isNarrator": false
                }
              ]
            }
            """);

        var service = new CharacterDiscoveryService(generator);

        var characters = await service.DiscoverAsync(
            CreateChapter(),
            []);

        var character = Assert.Single(characters);

        Assert.Equal("marcia-miller", character.Id);
        Assert.Equal("Marcia Miller", character.DisplayName);
        Assert.Equal(CharacterImportance.Major, character.Importance);
        Assert.Contains("Miller", character.Aliases);
        Assert.Contains("confident", character.VoiceTraits);

        Assert.Contains(
            "Miller looked up",
            generator.UserPrompt,
            StringComparison.Ordinal);

        Assert.Equal(
            "object",
            generator.Schema.GetProperty("type").GetString());
    }

    /// <summary>
    /// Verifies that unnamed speakers receive chapter-scoped identities.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_ScopesUnnamedCharacterId()
    {
        var generator = new StubStructuredTextGenerator(
            """
            {
              "characters": [
                {
                  "id": "prisoner-01",
                  "displayName": "Prisoner",
                  "aliases": [],
                  "description": "An unnamed prisoner.",
                  "voiceTraits": [
                    "strained"
                  ],
                  "voicePresentation": "male",
                  "isNamed": false,
                  "importance": "minor",
                  "isNarrator": false
                }
              ]
            }
            """);

        var service = new CharacterDiscoveryService(generator);

        var characters = await service.DiscoverAsync(
            CreateChapter(),
            []);

        var character = Assert.Single(characters);

        Assert.Equal(
            "chapter-001-prisoner-01",
            character.Id);

        var characterSchema = generator.Schema
            .GetProperty("properties")
            .GetProperty("characters")
            .GetProperty("items");

        Assert.True(
            characterSchema
                .GetProperty("properties")
                .TryGetProperty(
                    "isNamed",
                    out var isNamedSchema));

        Assert.Equal(
            "boolean",
            isNamedSchema.GetProperty("type").GetString());

        Assert.Contains(
            characterSchema
                .GetProperty("required")
                .EnumerateArray(),
            property =>
                property.GetString() == "isNamed");
    }
    /// <summary>
    /// Verifies that reserved speaker IDs are rejected.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_RejectsReservedCharacterId()
    {
        var generator = new StubStructuredTextGenerator(
            """
            {
              "characters": [
                {
                  "id": "unassigned",
                  "displayName": "Unassigned",
                  "aliases": [],
                  "description": "",
                  "voiceTraits": [],
                  "importance": "major",
                  "isNarrator": true
                }
              ]
            }
            """);

        var service = new CharacterDiscoveryService(generator);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DiscoverAsync(
                CreateChapter(),
                []));
        Assert.Equal(3, generator.Calls);
    }

    /// <summary>
    /// Verifies that a chapter-scoped ID returned for a named character is
    /// normalized to its persistent book-level identity.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_RemovesChapterScopeFromNamedCharacter()
    {
        var generator = new StubStructuredTextGenerator(
            """
            {
              "characters": [
                {
                  "id": "chapter-027-zad",
                  "displayName": "Zad",
                  "aliases": [],
                  "description": "A named Martian warrior.",
                  "voiceTraits": [
                    "aggressive"
                  ],
                  "voicePresentation": "male",
                  "isNamed": true,
                  "importance": "major",
                  "isNarrator": false
                }
              ]
            }
            """);

        var service = new CharacterDiscoveryService(generator);

        var characters = await service.DiscoverAsync(
            CreateChapter(),
            []);

        var character = Assert.Single(characters);

        Assert.Equal("zad", character.Id);
        Assert.True(character.IsNamed);
    }
    private static PreparedChapter CreateChapter()
    {
        return new PreparedChapter
        {
            ChapterId = "chapter-001",
            SourcePath = @"C:\Book\chapter-001.md",
            SourceSha256 = new string('a', 64),
            PreparationVersion = "1",
            SpokenText =
                "Miller looked up. \"We need to move,\" she said."
        };
    }

    private sealed class StubStructuredTextGenerator
        : IStructuredTextGenerator
    {
        private readonly string[] responses;
        public int Calls { get; private set; }

        public StubStructuredTextGenerator(params string[] responses)
        {
            this.responses = responses;
        }

        public string UserPrompt { get; private set; } = string.Empty;

        public JsonElement Schema { get; private set; }

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            JsonElement jsonSchema,
            CancellationToken cancellationToken = default)
        {
            if (systemPrompt.StartsWith("Independently verify character voice presentations"))
            {
                using var input = JsonDocument.Parse(userPrompt);
                return Task.FromResult(JsonSerializer.Serialize(new { characters = input.RootElement.GetProperty("characters")
                    .EnumerateArray().Select(item => new { id = item.GetProperty("Id").GetString(),
                        presentation = "unspecified", evidence = Array.Empty<string>() }).ToArray() }));
            }
            UserPrompt = userPrompt;
            Schema = jsonSchema.Clone();

            return Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
        }
    }
}
