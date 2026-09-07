using StoryCast.Application.Casting;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Tests.Casting;

/// <summary>
/// Tests presentation compatibility in automatic casting.
/// </summary>
public sealed class CastingPresentationValidatorTests
{
    /// <summary>
    /// Verifies that a known male role rejects a female voice.
    /// </summary>
    [Fact]
    public void Validate_MaleCharacterWithFemaleVoice_Throws()
    {
        var validator =
            new CastingAssignmentValidator();

        var registry = CreateRegistry("male");

        var voices = new[]
        {
            CreateVoice("female-voice", "female"),
            CreateVoice("narrator-voice", "female")
        };

        var assignments = new[]
        {
            new CastingAssignment
            {
                CharacterId = "character-1",
                VoiceId = "female-voice",
                Confidence = 0.9m
            },
            new CastingAssignment
            {
                CharacterId = "narrator",
                VoiceId = "narrator-voice",
                Confidence = 0.9m
            }
        };

        var exception = Assert.Throws<InvalidDataException>(
            () => validator.Validate(
                registry,
                voices,
                assignments));

        Assert.Contains(
            "requires a 'male' voice",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that unspecified roles may use either presentation.
    /// </summary>
    [Fact]
    public void Validate_UnspecifiedCharacter_AcceptsEitherPresentation()
    {
        var validator =
            new CastingAssignmentValidator();

        validator.Validate(
            CreateRegistry("unspecified"),
            [
                CreateVoice("female-voice", "female"),
                CreateVoice("narrator-voice", "male")
            ],
            [
                new CastingAssignment
                {
                    CharacterId = "character-1",
                    VoiceId = "female-voice",
                    Confidence = 0.9m
                },
                new CastingAssignment
                {
                    CharacterId = "narrator",
                    VoiceId = "narrator-voice",
                    Confidence = 0.9m
                }
            ]);
    }

    private static CharacterRegistry CreateRegistry(
        string presentation)
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
                    DisplayName = "Character One",
                    VoicePresentation = presentation
                }
            ],
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };
    }

    private static VoiceProfile CreateVoice(
        string id,
        string presentation)
    {
        return new VoiceProfile
        {
            Id = id,
            SamplePath = $"{id}.wav",
            Presentation = presentation
        };
    }
}
