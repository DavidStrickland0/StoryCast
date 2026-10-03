using System.Text.Json;
using System.Text.RegularExpressions;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Uses schema-constrained generation to discover chapter characters.
/// </summary>
public sealed partial class CharacterDiscoveryService
    : ICharacterDiscoveryService
{
    private const string SystemPrompt =
        """
        You identify characters for multi-voice audiobook production.

        Identify only people, creatures, artificial intelligences, or other
        entities that speak dialogue or serve as a first-person narrator in
        the supplied chapter.

        Reuse an existing character ID whenever the chapter refers to a known
        character by name, title, surname, alias, pronoun, or description.

        Create a new lowercase hyphenated ID only when the speaker is not an
        existing character. Set isNamed to true whenever the text provides a
        proper name, including a single-word name, surname, or identity-specific
        title. IDs derived from names such as woola, sarkoja, zad, dejah-thoris,
        or lorquas-ptomel must have isNamed set to true. Set isNamed to false
        only when the speaker is identified exclusively by a generic role or
        description such as a guard, prisoner, warrior, attendant, or
        dispatcher. Use descriptive IDs such as guard-01 or prisoner-01 for
        unnamed speakers. Do not reuse an unnamed speaker from Known characters
        unless the text explicitly establishes that it is the same individual.

        Do not create characters for places, objects, organizations, quoted
        documents, signs, memories without speech, or a normal third-person
        narrator.

        Voice presentation must be male, female, or unspecified. Use male
        or female only when names, pronouns, titles, or explicit description
        provide clear evidence. Common gender-associated names count as evidence.
        Reevaluate previously unspecified presentations from the current chapter.
        Never place male, female, or unspecified inside voiceTraits.
        Otherwise use unspecified.
        Do not infer traits unsupported by the supplied text. Voice traits
        should describe audible casting qualities supported by the text.
        Return only the JSON required by the supplied schema.
        """;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private static readonly JsonElement ResponseSchema =
        CreateResponseSchema();

    private readonly IStructuredTextGenerator generator;

    /// <summary>
    /// Initializes a character-discovery service.
    /// </summary>
    /// <param name="generator">
    /// The schema-constrained text generator.
    /// </param>
    public CharacterDiscoveryService(
        IStructuredTextGenerator generator)
    {
        this.generator =
            generator ??
            throw new ArgumentNullException(nameof(generator));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CharacterProfile>> DiscoverAsync(
        PreparedChapter chapter,
        IReadOnlyList<CharacterProfile> knownCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(knownCharacters);

        var knownCharacterSummary = knownCharacters.Select(
            character => new
            {
                character.Id,
                character.DisplayName,
                character.Aliases,
                character.Description,
                character.VoicePresentation,
                character.IsNamed,
                character.IsNarrator
            });

        var userPrompt =
            $"""
            Known characters:
            {JsonSerializer.Serialize(
                knownCharacterSummary,
                SerializerOptions)}

            Chapter ID:
            {chapter.ChapterId}

            Chapter text:
            <chapter>
            {chapter.SpokenText}
            </chapter>
            """;

        var responseText = await generator.GenerateAsync(
            SystemPrompt,
            userPrompt,
            ResponseSchema,
            cancellationToken);

        CharacterDiscoveryResponse response;

        try
        {
            response =
                JsonSerializer.Deserialize<CharacterDiscoveryResponse>(
                    responseText,
                    SerializerOptions) ??
                throw new InvalidDataException(
                    "Character discovery returned an empty document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Character discovery returned invalid JSON.",
                exception);
        }

        return ValidateAndMap(
            response,
            knownCharacters,
            chapter.ChapterId);
    }

    private static IReadOnlyList<CharacterProfile> ValidateAndMap(
        CharacterDiscoveryResponse response,
        IReadOnlyList<CharacterProfile> knownCharacters,
        string chapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        var knownById = knownCharacters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);

        var returnedIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var profiles = new List<CharacterProfile>(
            response.Characters.Count);

        foreach (var candidate in response.Characters)
        {
            var candidateId = CreateCandidateCharacterId(
                candidate,
                knownById);

            if (string.IsNullOrWhiteSpace(candidateId) ||
                !CharacterIdRegex().IsMatch(candidateId))
            {
                throw new InvalidDataException(
                    $"Character discovery returned invalid ID " +
                    $"'{candidateId}'.");
            }

            if (string.Equals(
                    candidateId,
                    "narrator",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    candidateId,
                    ProductionScriptSegmenter.UnassignedSpeakerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Character ID '{candidateId}' is reserved.");
            }

            if (string.IsNullOrWhiteSpace(candidate.DisplayName))
            {
                throw new InvalidDataException(
                    $"Character '{candidateId}' has no display name.");
            }

            if (!Enum.TryParse<CharacterImportance>(
                    candidate.Importance,
                    ignoreCase: true,
                    out var importance))
            {
                throw new InvalidDataException(
                    $"Character '{candidateId}' has invalid importance " +
                    $"'{candidate.Importance}'.");
            }

            var hasExistingCharacter =
                knownById.TryGetValue(
                    candidateId,
                    out var existing);

            var isNamed =
                candidate.IsNamed ||
                (hasExistingCharacter &&
                 existing!.IsNamed);

            var normalizedId =
                hasExistingCharacter && isNamed
                    ? existing!.Id
                    : ScopeCharacterId(
                        candidateId,
                        isNamed,
                        chapterId);

            if (!returnedIds.Add(normalizedId))
            {
                throw new InvalidDataException(
                    $"Character discovery returned duplicate ID " +
                    $"'{normalizedId}'.");
            }

            profiles.Add(
                new CharacterProfile
                {
                    Id = normalizedId,
                    DisplayName = candidate.DisplayName.Trim(),
                    Aliases = NormalizeValues(candidate.Aliases),
                    Description = candidate.Description.Trim(),
                    VoiceTraits = NormalizeVoiceTraits(
                        candidate.VoiceTraits),
                    VoicePresentation = ResolvePresentation(
                        candidate.VoicePresentation,
                        candidate.VoiceTraits),
                    IsNamed = isNamed,
                    Importance = importance,
                    IsNarrator = candidate.IsNarrator
                });
        }

        return profiles;
    }

    private static string CreateCandidateCharacterId(
        CharacterCandidate candidate,
        IReadOnlyDictionary<string, CharacterProfile> knownById)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id) &&
            knownById.TryGetValue(
                candidate.Id.Trim(),
                out var existing))
        {
            return existing.Id;
        }

        var source =
            candidate.IsNamed
                ? candidate.DisplayName
                : !string.IsNullOrWhiteSpace(candidate.Id) &&
                  CharacterIdRegex().IsMatch(candidate.Id)
                    ? candidate.Id
                    : candidate.DisplayName;

        return SlugifyCharacterId(source);
    }

    private static string SlugifyCharacterId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(
            value.Trim().ToLowerInvariant(),
            @"[^a-z0-9]+",
            "-",
            RegexOptions.CultureInvariant);

        return normalized.Trim('-');
    }

    private static string ScopeCharacterId(
        string characterId,
        bool isNamed,
        string chapterId)
    {
        var chapterPrefix = $"{chapterId}-";

        if (isNamed)
        {
            return ChapterScopedCharacterIdRegex().Replace(
                characterId,
                string.Empty);
        }

        if (characterId.StartsWith(
                chapterPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return characterId;
        }

        return $"{chapterPrefix}{characterId}";
    }

    private static string ResolvePresentation(
        string presentation,
        IReadOnlyList<string> voiceTraits)
    {
        var normalized = NormalizePresentation(
            presentation);

        if (!string.Equals(
                normalized,
                "unspecified",
                StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var presentations = voiceTraits
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "male" or "female")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return presentations.Length == 1
            ? presentations[0]
            : "unspecified";
    }

    private static IReadOnlyList<string> NormalizeVoiceTraits(
        IReadOnlyList<string> values)
    {
        return NormalizeValues(values)
            .Where(value =>
                !string.Equals(
                    value,
                    "male",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    value,
                    "female",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    value,
                    "unspecified",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
    private static string NormalizePresentation(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "male" => "male",
            "female" => "female",
            "unspecified" => "unspecified",
            _ => throw new InvalidDataException(
                $"Character discovery returned invalid voice " +
                $"presentation '{value}'.")
        };
    }
    private static IReadOnlyList<string> NormalizeValues(
        IReadOnlyList<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonElement CreateResponseSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "characters": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "id": {
                        "type": "string"
                      },
                      "displayName": {
                        "type": "string"
                      },
                      "aliases": {
                        "type": "array",
                        "items": {
                          "type": "string"
                        }
                      },
                      "description": {
                        "type": "string"
                      },
                      "voiceTraits": {
                        "type": "array",
                        "items": {
                          "type": "string"
                        }
                      },
                      "voicePresentation": {
                        "type": "string",
                        "enum": [
                          "male",
                          "female",
                          "unspecified"
                        ]
                      },
                      "isNamed": {
                        "type": "boolean"
                      },
                      "importance": {
                        "type": "string",
                        "enum": [
                          "minor",
                          "supporting",
                          "major"
                        ]
                      },
                      "isNarrator": {
                        "type": "boolean"
                      }
                    },
                    "required": [
                      "id",
                      "displayName",
                      "aliases",
                      "description",
                      "voiceTraits",
                      "voicePresentation",
                      "isNamed",
                      "importance",
                      "isNarrator"
                    ]
                  }
                }
              },
              "required": [
                "characters"
              ]
            }
            """);

        return document.RootElement.Clone();
    }

    [GeneratedRegex(
        @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CharacterIdRegex();

    [GeneratedRegex(
        @"^chapter-[0-9]+-",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase)]
    private static partial Regex ChapterScopedCharacterIdRegex();

    private sealed class CharacterDiscoveryResponse
    {
        public IReadOnlyList<CharacterCandidate> Characters { get; init; } = [];
    }

    private sealed class CharacterCandidate
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public IReadOnlyList<string> Aliases { get; init; } = [];

        public string Description { get; init; } = string.Empty;

        public IReadOnlyList<string> VoiceTraits { get; init; } = [];

        public string VoicePresentation { get; init; } =
            "unspecified";

        public bool IsNamed { get; init; }

        public string Importance { get; init; } = string.Empty;

        public bool IsNarrator { get; init; }
    }
}
