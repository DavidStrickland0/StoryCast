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
        Keep each rationale to one or two sentences, at most 500 characters.
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
        cancellationToken.ThrowIfCancellationRequested();

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

        var charactersById = registry.Characters.ToDictionary(character => character.Id, StringComparer.OrdinalIgnoreCase);
        var eligibleByRole = roleIds.ToDictionary(
            roleId => roleId,
            roleId => availableVoices
                .Where(voice => !charactersById.TryGetValue(roleId, out var character) ||
                    CastingAssignmentValidator.IsPresentationCompatible(character, voice))
                .Select(voice => voice.Id).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var role in eligibleByRole)
        {
            if (role.Value.Length == 0)
            {
                throw new InvalidDataException(
                    $"Role '{role.Key}' has no unused verified voices matching its required presentation.");
            }
        }

        // Presentation-constrained roles share the same candidate set. Catch shortages
        // before asking the model to make an impossible unique assignment.
        foreach (var group in eligibleByRole.GroupBy(role => string.Join("\n", role.Value)))
        {
            if (group.Count() > group.First().Value.Length)
            {
                throw new InvalidDataException(
                    $"Roles {string.Join(", ", group.Select(role => role.Key))} require {group.Count()} unique voices " +
                    $"but only {group.First().Value.Length} compatible unused voices are eligible.");
            }
        }

        if (roleIds.Length > 3)
        {
            return await AssignIndividuallyAsync(
                registry,
                availableVoices,
                existingAssignments,
                roleIds,
                eligibleByRole,
                cancellationToken);
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

            Allowed voice IDs for each role (mandatory):
            {JsonSerializer.Serialize(eligibleByRole, SerializerOptions)}

            Choose a voice only from that role's allowed list. A voice eligible for
            one role may be incompatible with another role.

            Eligible verified voices:
            {JsonSerializer.Serialize(
                voiceContext,
                SerializerOptions)}
            """;

        var schema = CreateResponseSchema(eligibleByRole);

        var attemptPrompt = userPrompt;
        InvalidDataException? lastFailure = null;

        for (var attempt = 1;
             attempt <= MaximumAttempts;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Automatic casting: attempt {attempt} of {MaximumAttempts} for {roleIds.Length} roles...");
            try
            {
                var responseText = await generator.GenerateAsync(
                    SystemPrompt,
                    attemptPrompt,
                    schema,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                var assignments =
                    ParseAssignments(responseText);

                if (assignments.Count != roleIds.Length ||
                    assignments.Select(assignment => assignment.CharacterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roleIds.Length ||
                    assignments.Any(assignment => !eligibleByRole.TryGetValue(assignment.CharacterId, out var allowed) ||
                        !allowed.Contains(assignment.VoiceId, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        "Return exactly the requested roles, each once, using only that role's allowed voice IDs.");
                }

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

    private async Task<IReadOnlyList<CastingAssignment>>
        AssignIndividuallyAsync(
            CharacterRegistry registry,
            IReadOnlyList<VoiceProfile> availableVoices,
            IReadOnlyList<CastingAssignment> existingAssignments,
            IReadOnlyList<string> roleIds,
            IReadOnlyDictionary<string, string[]> eligibleByRole,
            CancellationToken cancellationToken)
    {
        var charactersById = registry.Characters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);

        var voicesById = availableVoices.ToDictionary(
            voice => voice.Id,
            StringComparer.OrdinalIgnoreCase);

        var usedVoiceIds = existingAssignments
            .Select(assignment => assignment.VoiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var assignments = new List<CastingAssignment>(
            roleIds.Count);

        var orderedRoleIds = roleIds
            .OrderBy(
                roleId => eligibleByRole[roleId].Length)
            .ThenBy(
                roleId => roleId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var roleId in orderedRoleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allowedVoiceIds = eligibleByRole[roleId]
                .Where(voiceId => !usedVoiceIds.Contains(voiceId))
                .ToArray();

            if (allowedVoiceIds.Length == 0)
            {
                throw new InvalidDataException(
                    $"Role '{roleId}' has no remaining unused compatible " +
                    "voice.");
            }

            object role;

            if (charactersById.TryGetValue(
                    roleId,
                    out var character))
            {
                role = new
                {
                    character.Id,
                    character.DisplayName,
                    character.Aliases,
                    character.Description,
                    character.VoiceTraits,
                    character.VoicePresentation,
                    Importance = character.Importance.ToString(),
                    character.IsNarrator
                };
            }
            else
            {
                role = new
                {
                    Id = "narrator",
                    DisplayName = "Narrator",
                    Aliases = Array.Empty<string>(),
                    Description = "Primary audiobook narrative voice.",
                    VoiceTraits = new[]
                    {
                        "clear",
                        "consistent",
                        "sustained"
                    },
                    VoicePresentation = "unspecified",
                    Importance = "Primary",
                    IsNarrator = true
                };
            }

            var candidateVoices = allowedVoiceIds
                .Select(voiceId => voicesById[voiceId])
                .Select(
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
                    })
                .ToArray();

            var userPrompt =
                $"""
                Role:
                {JsonSerializer.Serialize(
                    role,
                    SerializerOptions)}

                Allowed voice IDs:
                {JsonSerializer.Serialize(
                    allowedVoiceIds,
                    SerializerOptions)}

                Eligible verified voices:
                {JsonSerializer.Serialize(
                    candidateVoices,
                    SerializerOptions)}

                Choose exactly one voiceId from Allowed voice IDs.
                The application already knows the role ID. Do not return it.
                """;

            var schema = JsonSerializer.SerializeToElement(
                new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        voiceId = new
                        {
                            type = "string",
                            @enum = allowedVoiceIds
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
                            maxLength = 500
                        }
                    },
                    required = new[]
                    {
                        "voiceId",
                        "confidence",
                        "rationale"
                    }
                });

            InvalidDataException? lastFailure = null;
            CastingAssignment? completedAssignment = null;

            for (var attempt = 1;
                 attempt <= MaximumAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Console.WriteLine(
                    $"Automatic casting: role '{roleId}', attempt " +
                    $"{attempt} of {MaximumAttempts}...");

                try
                {
                    var responseText = await generator.GenerateAsync(
                        SystemPrompt,
                        userPrompt,
                        schema,
                        cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        throw new InvalidDataException(
                            "Automatic casting returned an empty document.");
                    }

                    CastingSelectionResponse selection;

                    try
                    {
                        selection =
                            JsonSerializer.Deserialize<CastingSelectionResponse>(
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

                    if (string.IsNullOrWhiteSpace(selection.VoiceId) ||
                        selection.Rationale is null)
                    {
                        throw new InvalidDataException(
                            "Casting selection requires a voice ID and " +
                            "rationale.");
                    }

                    if (!allowedVoiceIds.Contains(
                            selection.VoiceId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Voice '{selection.VoiceId}' is not allowed for " +
                            $"role '{roleId}'.");
                    }

                    if (selection.Confidence < 0 || selection.Confidence > 1)
                    {
                        throw new InvalidDataException(
                            $"Role '{roleId}' has invalid casting confidence " +
                            $"{selection.Confidence}.");
                    }

                    if (usedVoiceIds.Contains(selection.VoiceId))
                    {
                        throw new InvalidDataException(
                            $"Voice '{selection.VoiceId}' is already assigned.");
                    }

                    completedAssignment =
                        new CastingAssignment
                        {
                            CharacterId = roleId,
                            VoiceId = selection.VoiceId,
                            Confidence = selection.Confidence,
                            Rationale = selection.Rationale.Trim(),
                            IsLocked = false,
                            Synthesis = new CastingSynthesisSettings
                            {
                                Exaggeration = string.Equals(
                                    roleId,
                                    "narrator",
                                    StringComparison.OrdinalIgnoreCase)
                                        ? 0.4m
                                        : 0.65m,
                                CfgWeight = 0.5m,
                                Temperature = 0.7m
                            }
                        };

                    break;
                }
                catch (InvalidDataException exception)
                    when (attempt < MaximumAttempts)
                {
                    lastFailure = exception;

                    Console.WriteLine(
                        $"Automatic casting: role '{roleId}', attempt " +
                        $"{attempt} rejected: {exception.Message}");

                    userPrompt =
                        $"""
                        {userPrompt}

                        The previous response was invalid:
                        {exception.Message}

                        Return one corrected selection using exactly one
                        voiceId from the allowed list.
                        """;
                }
                catch (InvalidDataException exception)
                {
                    lastFailure = exception;

                    Console.WriteLine(
                        $"Automatic casting: role '{roleId}', attempt " +
                        $"{attempt} rejected: {exception.Message}");
                }
            }

            if (completedAssignment is null)
            {
                throw new InvalidDataException(
                    $"Automatic casting failed for role '{roleId}' after " +
                    $"{MaximumAttempts} attempts. Final failure: " +
                    $"{lastFailure?.Message}",
                    lastFailure);
            }

            assignments.Add(completedAssignment);
            usedVoiceIds.Add(completedAssignment.VoiceId);
        }

        return assignments;
    }

    private static IReadOnlyList<CastingAssignment>
        ParseAssignments(
            string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidDataException(
                "Automatic casting returned an empty document.");
        }

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

        if (response.Assignments is null || response.Assignments.Any(
                assignment => assignment is null ||
                    string.IsNullOrWhiteSpace(assignment.CharacterId) ||
                    string.IsNullOrWhiteSpace(assignment.VoiceId) ||
                    assignment.Rationale is null))
        {
            throw new InvalidDataException(
                "Automatic casting requires non-null assignments, non-empty role and voice IDs, and non-null rationale.");
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

    private static JsonElement CreateResponseSchema(IReadOnlyDictionary<string, string[]> eligibleByRole)
    {
        var roleSchemas = eligibleByRole.Select(role => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                characterId = new { type = "string", @enum = new[] { role.Key } },
                voiceId = new { type = "string", @enum = role.Value },
                confidence = new { type = "number", minimum = 0, maximum = 1 },
                rationale = new { type = "string", maxLength = 500 }
            },
            required = new[] { "characterId", "voiceId", "confidence", "rationale" }
        }).ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                assignments = new
                {
                    type = "array",
                    minItems = eligibleByRole.Count,
                    maxItems = eligibleByRole.Count,
                    items = new { anyOf = roleSchemas }
                }
            },
            required = new[] { "assignments" }
        });
    }

    private sealed class CastingSelectionResponse
    {
        public string VoiceId { get; init; } =
            string.Empty;

        public decimal Confidence { get; init; }

        public string Rationale { get; init; } =
            string.Empty;
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
