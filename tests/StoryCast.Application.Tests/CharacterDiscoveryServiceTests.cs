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
                  "id": "narrator",
                  "displayName": "Narrator",
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
