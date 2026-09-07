using System.Text.Json;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>
/// Uses schema-constrained generation to cast audiobook voices.
/// </summary>
public sealed class AutomaticCastingService
    : ICastingService
{
    private const string SystemPrompt =
        """
        You cast voices for a multi-voice audiobook.

        Assign exactly one unique eligible voice to every supplied role.
        Never assign the same voice to more than one role.

        Match voice presentation to clearly implied character presentation.
        Match qualities, apparent age, accent, and suitable roles when metadata
        supports the choice. Do not invent voice properties that are absent.

        The narrator should be clear and suitable for sustained narration.
        Prefer a voice explicitly marked narratorSuitable for the narrator, but
        select the best remaining verified voice when none is marked.

        Confidence must reflect the available evidence. Rationale must briefly
        explain the match using only supplied character and voice metadata.
        Return only JSON matching the supplied schema.
        """;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly IStructuredTextGenerator generator;
    private readonly CastingAssignmentValidator validator;

    /// <summary>
    /// Initializes an automatic casting service.
    /// </summary>
    /// <param name="generator">
    /// The schema-constrained text generator.
    /// </param>
    /// <param name="validator">
    /// The deterministic assignment validator.
    /// </param>
    public AutomaticCastingService(
        IStructuredTextGenerator generator,
        CastingAssignmentValidator validator)
    {
        this.generator = generator ??
            throw new ArgumentNullException(nameof(generator));

        this.validator = validator ??
            throw new ArgumentNullException(nameof(validator));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CastingAssignment>> AssignAsync(
        CharacterRegistry registry,
        IReadOnlyList<VoiceProfile> voices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(voices);

        var roleIds = registry.Characters
            .Select(character => character.Id)
            .Append("narrator")
            .ToArray();

        if (voices.Count < roleIds.Length)
        {
            throw new InvalidDataException(
                $"Casting requires {roleIds.Length} unique voices but " +
                $"only {voices.Count} are eligible.");
        }

        var roles = registry.Characters
            .Select(
                character => new
                {
                    character.Id,
                    character.DisplayName,
                    character.Aliases,
                    character.Description,
                    character.VoiceTraits,
                    character.VoicePresentation,
                    Importance =
                        character.Importance.ToString(),
                    character.IsNarrator
                })
            .Cast<object>()
            .Append(
                new
                {
                    Id = "narrator",
                    DisplayName = "Narrator",
                    Aliases = Array.Empty<string>(),
                    Description =
                        "Primary audiobook narrative voice.",
                    VoiceTraits = new[]
                    {
                        "clear",
                        "consistent",
                        "sustained"
                    },
                    Importance = "Primary",
                    IsNarrator = true
                })
            .ToArray();

        var voiceContext = voices.Select(
            voice => new
            {
                voice.Id,
                voice.Language,
                voice.Accent,
                voice.ApparentAge,
                voice.Presentation,
                voice.Qualities,
                voice.SuitableRoles,
                voice.NarratorSuitable
            });

        var userPrompt =
            $"""
            Roles:
            {JsonSerializer.Serialize(
                roles,
                SerializerOptions)}

            Eligible verified voices:
            {JsonSerializer.Serialize(
                voiceContext,
                SerializerOptions)}
            """;

        var schema = CreateResponseSchema(
            roleIds,
            voices.Select(voice => voice.Id).ToArray());

        var responseText = await generator.GenerateAsync(
            SystemPrompt,
            userPrompt,
            schema,
            cancellationToken);

        CastingResponse response;

        try
        {
            response =
                JsonSerializer.Deserialize<CastingResponse>(
                    responseText,
                    SerializerOptions) ??
                throw new InvalidDataException(
                    "Automatic casting returned an empty document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Automatic casting returned invalid JSON.",
                exception);
        }

        var assignments = response.Assignments.Select(
            assignment => new CastingAssignment
            {
                CharacterId = assignment.CharacterId,
                VoiceId = assignment.VoiceId,
                Confidence = assignment.Confidence,
                Rationale = assignment.Rationale.Trim(),
                IsLocked = false
            }).ToArray();

        validator.Validate(
            registry,
            voices,
            assignments);

        return assignments;
    }

    private static JsonElement CreateResponseSchema(
        IReadOnlyList<string> roleIds,
        IReadOnlyList<string> voiceIds)
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
                        minItems = roleIds.Count,
                        maxItems = roleIds.Count,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                characterId = new
                                {
                                    type = "string",
                                    @enum = roleIds
                                },
                                voiceId = new
                                {
                                    type = "string",
                                    @enum = voiceIds
                                },
                                confidence = new
                                {
                                    type = "number",
                                    minimum = 0,
                                    maximum = 1
                                },
                                rationale = new
                                {
                                    type = "string"
                                }
                            },
                            required = new[]
                            {
                                "characterId",
                                "voiceId",
                                "confidence",
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

    private sealed class CastingResponse
    {
        public IReadOnlyList<CastingAssignmentResponse>
            Assignments
        { get; init; } = [];
    }

    private sealed class CastingAssignmentResponse
    {
        public string CharacterId { get; init; } =
            string.Empty;

        public string VoiceId { get; init; } =
            string.Empty;

        public decimal Confidence { get; init; }

        public string Rationale { get; init; } =
            string.Empty;
    }
}
