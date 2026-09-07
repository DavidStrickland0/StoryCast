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
        private readonly string response;

        public StubStructuredTextGenerator(string response)
        {
            this.response = response;
        }

        public string UserPrompt { get; private set; } = string.Empty;

        public JsonElement Schema { get; private set; }

        public Task<string> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            JsonElement jsonSchema,
            CancellationToken cancellationToken = default)
        {
            UserPrompt = userPrompt;
            Schema = jsonSchema.Clone();

            return Task.FromResult(response);
        }
    }
}
