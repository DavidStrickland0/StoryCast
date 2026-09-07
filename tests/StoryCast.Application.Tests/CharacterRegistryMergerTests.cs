using StoryCast.Application.Attribution;
using StoryCast.Domain.Characters;

namespace StoryCast.Application.Tests.Attribution;

/// <summary>
/// Tests deterministic character-registry reconciliation.
/// </summary>
public sealed class CharacterRegistryMergerTests
{
    /// <summary>
    /// Verifies that an existing identity is enriched without changing its ID.
    /// </summary>
    [Fact]
    public void Merge_EnrichesExistingCharacter()
    {
        var registry = CreateRegistry(
            new CharacterProfile
            {
                Id = "marcia-miller",
                DisplayName = "Miller",
                Aliases = ["Corporal Miller"],
                Description = "A Marine.",
                VoiceTraits = ["controlled"],
                Importance = CharacterImportance.Supporting,
                IsNarrator = false
            });

        var discovery = new CharacterProfile
        {
            Id = "marcia-miller",
            DisplayName = "Marcia Miller",
            Aliases = ["Marcia"],
            Description = "A different generated description.",
            VoiceTraits = ["confident"],
            Importance = CharacterImportance.Major,
            IsNarrator = true
        };

        var merger = new CharacterRegistryMerger();

        var result = merger.Merge(
            registry,
            "test-book",
            "chapter-002",
            new string('b', 64),
            [discovery]);

        var character = Assert.Single(result.Characters);

        Assert.Equal("marcia-miller", character.Id);
        Assert.Equal("Miller", character.DisplayName);
        Assert.Equal("A Marine.", character.Description);
        Assert.Contains("Marcia Miller", character.Aliases);
        Assert.Contains("Marcia", character.Aliases);
        Assert.Contains("controlled", character.VoiceTraits);
        Assert.Contains("confident", character.VoiceTraits);
        Assert.Equal(CharacterImportance.Major, character.Importance);
        Assert.True(character.IsNarrator);
    }

    /// <summary>
    /// Verifies that a newly proposed ID maps to a unique known alias.
    /// </summary>
    [Fact]
    public void Merge_MapsNewIdThroughKnownAlias()
    {
        var registry = CreateRegistry(
            new CharacterProfile
            {
                Id = "marcia-miller",
                DisplayName = "Marcia Miller",
                Aliases = ["Miller", "Corporal Miller"],
                Description = "A Marine corporal.",
                VoiceTraits = [],
                Importance = CharacterImportance.Major,
                IsNarrator = false
            });

        var discovery = new CharacterProfile
        {
            Id = "corporal-miller",
            DisplayName = "Corporal Miller",
            Aliases = [],
            Description = "",
            VoiceTraits = ["restrained"],
            Importance = CharacterImportance.Supporting,
            IsNarrator = false
        };

        var merger = new CharacterRegistryMerger();

        var result = merger.Merge(
            registry,
            "test-book",
            "chapter-002",
            new string('b', 64),
            [discovery]);

        var character = Assert.Single(result.Characters);

        Assert.Equal("marcia-miller", character.Id);
        Assert.Contains("restrained", character.VoiceTraits);
    }

    /// <summary>
    /// Verifies that unrelated discoveries create new identities.
    /// </summary>
    [Fact]
    public void Merge_AddsNewCharacter()
    {
        var merger = new CharacterRegistryMerger();

        var result = merger.Merge(
            registry: null,
            bookId: "test-book",
            chapterId: "chapter-001",
            sourceSha256: new string('a', 64),
            discoveries:
            [
                new CharacterProfile
                {
                    Id = "elias-thorne",
                    DisplayName = "Elias Thorne",
                    Aliases = ["Thorne"],
                    Description = "A Marine sergeant.",
                    VoiceTraits = ["steady"],
                    Importance = CharacterImportance.Major,
                    IsNarrator = true
                }
            ]);

        var character = Assert.Single(result.Characters);

        Assert.Equal("elias-thorne", character.Id);
        Assert.Equal(
            new string('a', 64),
            result.ProcessedChapterHashes["chapter-001"]);
    }

    /// <summary>
    /// Verifies that ambiguous alias matches are rejected.
    /// </summary>
    [Fact]
    public void Merge_RejectsAmbiguousIdentityMatch()
    {
        var registry = new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "test-book",
            Characters =
            [
                CreateMinorCharacter("guard-01", "Guard"),
                CreateMinorCharacter("guard-02", "Guard")
            ],
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };

        var discovery = CreateMinorCharacter(
            "new-guard",
            "Guard");

        var merger = new CharacterRegistryMerger();

        Assert.Throws<InvalidDataException>(
            () => merger.Merge(
                registry,
                "test-book",
                "chapter-002",
                new string('b', 64),
                [discovery]));
    }

    private static CharacterRegistry CreateRegistry(
        CharacterProfile character)
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "test-book",
            Characters = [character],
            ProcessedChapterHashes =
                new Dictionary<string, string>
                {
                    ["chapter-001"] = new string('a', 64)
                }
        };
    }

    private static CharacterProfile CreateMinorCharacter(
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
            Importance = CharacterImportance.Minor,
            IsNarrator = false
        };
    }
}
