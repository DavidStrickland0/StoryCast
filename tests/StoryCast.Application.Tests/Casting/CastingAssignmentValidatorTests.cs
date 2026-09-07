using StoryCast.Application.Casting;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Tests.Casting;

/// <summary>
/// Tests strict validation of generated casting assignments.
/// </summary>
public sealed class CastingAssignmentValidatorTests
{
    /// <summary>
    /// Verifies that complete unique casting is accepted.
    /// </summary>
    [Fact]
    public void Validate_CompleteUniqueAssignments_AcceptsCasting()
    {
        var validator = new CastingAssignmentValidator();

        validator.Validate(
            CreateRegistry(),
            CreateVoices(),
            [
                new CastingAssignment
                {
                    CharacterId = "character-1",
                    VoiceId = "voice-1",
                    Confidence = 0.9m
                },
                new CastingAssignment
                {
                    CharacterId = "narrator",
                    VoiceId = "voice-2",
                    Confidence = 0.8m
                }
            ]);
    }

    /// <summary>
    /// Verifies that one voice cannot be assigned to multiple roles.
    /// </summary>
    [Fact]
    public void Validate_DuplicateVoice_ThrowsInvalidDataException()
    {
        var validator = new CastingAssignmentValidator();

        var exception = Assert.Throws<InvalidDataException>(
            () => validator.Validate(
                CreateRegistry(),
                CreateVoices(),
                [
                    new CastingAssignment
                    {
                        CharacterId = "character-1",
                        VoiceId = "voice-1",
                        Confidence = 0.9m
                    },
                    new CastingAssignment
                    {
                        CharacterId = "narrator",
                        VoiceId = "voice-1",
                        Confidence = 0.8m
                    }
                ]));

        Assert.Contains(
            "more than once",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that every character and the narrator must be cast.
    /// </summary>
    [Fact]
    public void Validate_MissingNarrator_ThrowsInvalidDataException()
    {
        var validator = new CastingAssignmentValidator();

        var exception = Assert.Throws<InvalidDataException>(
            () => validator.Validate(
                CreateRegistry(),
                CreateVoices(),
                [
                    new CastingAssignment
                    {
                        CharacterId = "character-1",
                        VoiceId = "voice-1",
                        Confidence = 0.9m
                    }
                ]));

        Assert.Contains(
            "missing required roles",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CharacterRegistry CreateRegistry()
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "book-1",
            Characters =
            [
                new CharacterProfile
                {
                    Id = "character-1",
                    DisplayName = "Character One"
                }
            ],
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };
    }

    private static IReadOnlyList<VoiceProfile> CreateVoices()
    {
        return
        [
            new VoiceProfile
            {
                Id = "voice-1",
                SamplePath = "voice-1.wav"
            },
            new VoiceProfile
            {
                Id = "voice-2",
                SamplePath = "voice-2.wav"
            }
        ];
    }
}
