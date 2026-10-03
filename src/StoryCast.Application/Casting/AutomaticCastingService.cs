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
    private const int MaximumAttempts = 3;

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
        Keep each rationale to one or two sentences, at most 600 characters.
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
        IReadOnlyList<CastingAssignment> existingAssignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(existingAssignments);

        var existingRoleIds = existingAssignments
            .Select(assignment => assignment.CharacterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingVoiceIds = existingAssignments
            .Select(assignment => assignment.VoiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingCharacters = registry.Characters
            .Where(character => !existingRoleIds.Contains(character.Id))
            .ToArray();

        var narratorIsMissing =
            !existingRoleIds.Contains("narrator");

        var roleIds = missingCharacters
            .Select(character => character.Id)
            .Concat(
                narratorIsMissing
                    ? ["narrator"]
                    : [])
            .ToArray();

        if (roleIds.Length == 0)
        {
            return [];
        }

        var availableVoices = voices
            .Where(voice => !existingVoiceIds.Contains(voice.Id))
            .ToArray();

        if (availableVoices.Length < roleIds.Length)
        {
            throw new InvalidDataException(
                $"Casting requires {roleIds.Length} additional unique " +
                $"voices but only {availableVoices.Length} unused verified " +
                "voices are eligible.");
        }

        var roles = new List<object>();

        roles.AddRange(
            missingCharacters.Select(
                character => (object)new
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
                }));

        if (narratorIsMissing)
        {
            roles.Add(
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
                    VoicePresentation = "unspecified",
                    Importance = "Primary",
                    IsNarrator = true
                });
        }

        var voiceContext = availableVoices.Select(
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
            availableVoices
                .Select(voice => voice.Id)
                .ToArray());

        var attemptPrompt = userPrompt;
        InvalidDataException? lastFailure = null;

        for (var attempt = 1;
             attempt <= MaximumAttempts;
             attempt++)
        {
            Console.WriteLine(
                $"Automatic casting: attempt {attempt}/{MaximumAttempts} " +
                $"for {roleIds.Length} roles using {availableVoices.Length} eligible voices...");
            var responseText = await generator.GenerateAsync(
                SystemPrompt,
                attemptPrompt,
                schema,
                cancellationToken);

            try
            {
                var assignments =
                    ParseAssignments(responseText);

                var completeAssignments =
                    existingAssignments
                        .Concat(assignments)
                        .ToArray();

                var activeRoleIds = registry.Characters
                    .Select(character => character.Id)
                    .Append("narrator")
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

                var activeAssignments = completeAssignments
                    .Where(
                        assignment => activeRoleIds.Contains(
                            assignment.CharacterId))
                    .ToArray();

                validator.Validate(
                    registry,
                    voices,
                    activeAssignments);

                return assignments;
            }
            catch (InvalidDataException exception)
            {
                lastFailure = exception;
                Console.WriteLine(
                    $"Automatic casting: attempt {attempt}/{MaximumAttempts} " +
                    $"rejected: {exception.Message}");

                attemptPrompt =
                    $"""
                    {userPrompt}

                    The previous casting response was invalid:
                    {exception.Message}

                    Return a corrected complete assignment for every supplied
                    role. Use each role exactly once and each eligible voice
                    at most once. Do not return roles omitted from the supplied
                    role list.
                    """;
            }
        }

        throw new InvalidDataException(
            $"Automatic casting failed after {MaximumAttempts} attempts. " +
            $"Last failure: {lastFailure?.Message}",
            lastFailure);
    }

    private static IReadOnlyList<CastingAssignment>
        ParseAssignments(
            string responseText)
    {
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
                $"Automatic casting returned invalid JSON: {exception.Message}",
                exception);
        }

        return response.Assignments.Select(
            assignment => new CastingAssignment
            {
                CharacterId = assignment.CharacterId,
                VoiceId = assignment.VoiceId,
                Confidence = assignment.Confidence,
                Rationale = assignment.Rationale.Trim(),
                IsLocked = false,
                Synthesis = new CastingSynthesisSettings
                {
                    Exaggeration = string.Equals(
                        assignment.CharacterId,
                        "narrator",
                        StringComparison.OrdinalIgnoreCase)
                            ? 0.4m
                            : 0.65m,
                    CfgWeight = 0.5m,
                    Temperature = 0.7m
                }
            }).ToArray();
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
                                    type = "string",
                                    maxLength = 600
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
