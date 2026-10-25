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
        Return each speaker exactly once. Character IDs must be unique.

        Identify only people, creatures, artificial intelligences, or other
        entities that speak dialogue or serve as a first-person narrator in
        the supplied chapter.

        Some books use [square brackets] for audible AI, system, or android
        speech instead of quotation marks. Discover those speakers when the
        surrounding story establishes that convention. Distinguish a ship AI
        from an android when context does; a speaking mode is not a new identity.
        Bracketed chapter subtitles and editorial notes are not speakers.
        A message such as [Forever1stCommenter (Earth): He has a point.] has
        Forever1stCommenter as its speaker; Earth is origin metadata, not an
        alias or speaker. Reuse the same named identity across messages and
        chapters. Do not merge a named commenter with the system or android.

        Include participants in untagged back-and-forth dialogue, not just
        characters with explicit "said" tags. Infer distinct participants from
        nearby actions, questions and replies, forms of address, and the full
        exchange. Successive quoted paragraphs in an established two-person
        conversation normally alternate; do not collapse both participants
        into one character because attribution tags are absent. Do not invent
        extra participants for a continuing speech or alternate mechanically
        in a group conversation. When the text establishes a distinct speaker
        without a name, create an unnamed identity using the rules below.

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

        The production narrator is managed separately. Never return the reserved
        ID narrator or copy it from Known characters. For a first-person narrator
        who is also a story character, use that person's actual character identity
        and name instead of creating a generic narrator character.

        Voice presentation must be male, female, or unspecified. Use male
        or female only when pronouns or explicit description provide clear
        evidence referring to this character. Names alone are not evidence.
        Reevaluate previously unspecified presentations from the current chapter.
        Preserve an existing male or female voice presentation for known characters.
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

        const int maximumPassageCharacters = 6000;
        if (chapter.SpokenText.Length <= maximumPassageCharacters)
            return await DiscoverPassageAsync(chapter, knownCharacters, cancellationToken);
        var discoveries = new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < chapter.SpokenText.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(maximumPassageCharacters, chapter.SpokenText.Length - offset);
            if (offset + length < chapter.SpokenText.Length)
            {
                var boundary = chapter.SpokenText.LastIndexOf('\n', offset + length - 1, length);
                if (boundary >= offset + length / 2) length = boundary - offset + 1;
            }
            var passage = new PreparedChapter { ChapterId = chapter.ChapterId, SourcePath = chapter.SourcePath,
                SourceSha256 = chapter.SourceSha256, PreparationVersion = chapter.PreparationVersion,
                SpokenText = chapter.SpokenText.Substring(offset, length) };
            Console.WriteLine($"Character discovery passage {offset + 1}-{offset + length}/{chapter.SpokenText.Length}");
            var known = knownCharacters.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var item in discoveries.Values) known[item.Id] = item;
            foreach (var item in await DiscoverPassageAsync(passage, known.Values.ToArray(), cancellationToken,
                         discoveries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                         offset == 0 ? "" : chapter.SpokenText.Substring(Math.Max(0, offset - 600), Math.Min(600, offset))))
                discoveries[item.Id] = item;
            offset += length;
        }
        return discoveries.Values.ToArray();
    }

    private async Task<IReadOnlyList<CharacterProfile>> DiscoverPassageAsync(PreparedChapter chapter,
        IReadOnlyList<CharacterProfile> knownCharacters, CancellationToken cancellationToken,
        IReadOnlySet<string>? carriedIdentities = null, string previousContext = "")
    {

        // Keep the complete registry for identity validation, but do not fill a
        // small local model's context with unrelated characters from other chapters.
        var relevant = knownCharacters.Where(character => character.IsNarrator || carriedIdentities?.Contains(character.Id) == true ||
            character.Aliases.Append(character.DisplayName).Any(name => !string.IsNullOrWhiteSpace(name) &&
                Regex.IsMatch(chapter.SpokenText, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)", RegexOptions.IgnoreCase))).ToArray();
        Console.WriteLine($"Character discovery context: {relevant.Length}/{knownCharacters.Count} saved identities, {chapter.SpokenText.Length} source characters.");
        var knownCharacterSummary = relevant.Where(character => !string.Equals(character.Id, "narrator", StringComparison.OrdinalIgnoreCase)).Select(
            character => new
            {
                character.Id,
                character.DisplayName,
                character.Aliases,
                Description = character.Description.Length <= 240 ? character.Description : character.Description[..240],
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

            Previous passage context (identity continuity only; discover speakers from Chapter text below):
            {previousContext}

            Chapter text:
            <chapter>
            {chapter.SpokenText}
            </chapter>
            """;

        var originalPrompt = userPrompt;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var responseText = await generator.GenerateAsync(
                    SystemPrompt, userPrompt, ResponseSchema, cancellationToken);
                CharacterDiscoveryResponse response;
                try
                {
                    response = JsonSerializer.Deserialize<CharacterDiscoveryResponse>(
                        responseText, SerializerOptions) ??
                        throw new InvalidDataException("Character discovery returned an empty document.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Character discovery returned invalid JSON.", exception);
                }
                var profiles = ValidateAndMap(response, knownCharacters, chapter.ChapterId, chapter.SpokenText);
                var verified = await new CharacterPresentationVerifier(generator)
                    .VerifyAsync(chapter.SpokenText, profiles, cancellationToken);
                return profiles.Select(profile =>
                {
                    var presentation = verified[profile.Id];
                    var known = knownCharacters.SingleOrDefault(character =>
                        string.Equals(character.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
                    if (known?.VoicePresentation is "male" or "female")
                    {
                        if (presentation != "unspecified" && presentation != known.VoicePresentation)
                            throw new InvalidDataException($"Character '{profile.Id}' has source-supported presentation '{presentation}' " +
                                $"conflicting with saved '{known.VoicePresentation}'. Review the saved character profile and chapter prose.");
                        presentation = known.VoicePresentation;
                    }
                    if (presentation != profile.VoicePresentation)
                        Console.WriteLine($"Character '{profile.Id}': evidence review changed proposed presentation " +
                            $"'{profile.VoicePresentation}' to '{presentation}'.");
                    return new CharacterProfile {
                        Id = profile.Id, DisplayName = profile.DisplayName, Aliases = profile.Aliases,
                        Description = profile.Description, VoiceTraits = profile.VoiceTraits,
                        VoicePresentation = presentation, IsNamed = profile.IsNamed,
                        Importance = profile.Importance, IsNarrator = profile.IsNarrator
                    };
                }).ToArray();
            }
            catch (InvalidDataException exception) when (attempt < 3)
            {
                Console.WriteLine($"Character discovery correction {attempt}/3: {exception.Message}");
                userPrompt = originalPrompt + "\nYour previous response was invalid: " + exception.Message +
                    "\nReturn a complete corrected character list. Use each ID once. " +
                    "Merge repeated entries for the same speaker; use distinct IDs for distinct speakers.";
            }
            catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Character discovery transport attempt {attempt}/3: {exception.Message}");
                if (attempt == 3)
                    throw new IOException("Character discovery could not reach a usable Ollama response after three bounded attempts; chapter progress is preserved.", exception);
                userPrompt = originalPrompt;
            }
        }
        throw new InvalidOperationException("Character discovery attempts exhausted.");
    }

    private static IReadOnlyList<CharacterProfile> ValidateAndMap(
        CharacterDiscoveryResponse response,
        IReadOnlyList<CharacterProfile> knownCharacters,
        string chapterId,
        string chapterText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        var knownById = knownCharacters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);

        var returnedIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var profiles = new List<CharacterProfile>(
            response.Characters.Count);
        var labeledNames = Regex.Matches(chapterText, @"\[(?<label>[^\[\]:\r\n]{1,150}):")
            .Select(match => Regex.Replace(match.Groups["label"].Value.Trim(), @"\s*\([^()]*\)\s*$", "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            if (string.Equals(candidateId, "narrator", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Ignoring the production narrator returned by character discovery; retaining its existing voice assignment.");
                continue;
            }

            if (string.Equals(
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
            var explicitChatHandle = labeledNames.Contains(candidate.DisplayName.Trim());
            if (explicitChatHandle)
            {
                // Explicit handles are named identities, even if generation
                // incorrectly reports isNamed=false. Prefer a stable existing
                // identity over new or repeatedly chapter-prefixed IDs.
                var match = knownCharacters.Where(character =>
                        character.DisplayName.Equals(candidate.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(character => character.Id.StartsWith("chapter-", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(character => character.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (match is not null)
                {
                    existing = match;
                    hasExistingCharacter = true;
                }
            }

            // A known named identity stays named even if the model incorrectly
            // labels the new occurrence unnamed or proposes a chapter-scoped ID.
            if (!hasExistingCharacter)
            {
                var namedMatches = knownCharacters.Where(character => character.IsNamed &&
                    character.Aliases.Append(character.DisplayName).Any(name =>
                        string.Equals(name.Trim(), candidate.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
                if (namedMatches.Length == 1)
                {
                    existing = namedMatches[0];
                    hasExistingCharacter = true;
                }
                else if (namedMatches.Length > 1)
                    throw new InvalidDataException($"Character '{candidate.DisplayName}' ambiguously matches known named identities.");
            }

            var isNamed =
                explicitChatHandle || candidate.IsNamed ||
                (hasExistingCharacter &&
                 existing!.IsNamed);

            var normalizedId =
                hasExistingCharacter && isNamed
                    ? existing!.Id
                    : ScopeCharacterId(
                        candidateId,
                        isNamed,
                        chapterId);

            var presentation = ResolvePresentation(candidate.VoicePresentation, candidate.VoiceTraits);
            if (hasExistingCharacter && existing!.VoicePresentation is "male" or "female")
            {
                if (presentation != "unspecified" && presentation != existing.VoicePresentation)
                    Console.WriteLine($"Character '{existing.Id}': keeping established voice presentation " +
                        $"'{existing.VoicePresentation}'; ignoring rediscovery '{presentation}'.");
                presentation = existing.VoicePresentation;
            }

            if (!returnedIds.Add(normalizedId))
            {
                var previous = profiles.Single(profile => string.Equals(profile.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
                var sameKnownIdentity = hasExistingCharacter && existing!.IsNamed && previous.IsNamed &&
                    existing.Aliases.Append(existing.DisplayName).Any(name =>
                        string.Equals(name.Trim(), previous.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    existing.Aliases.Append(existing.DisplayName).Any(name =>
                        string.Equals(name.Trim(), candidate.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
                if ((sameKnownIdentity || (explicitChatHandle && previous.DisplayName.Equals(candidate.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))) &&
                    (sameKnownIdentity || previous.IsNarrator == candidate.IsNarrator) &&
                    (previous.VoicePresentation == presentation || previous.VoicePresentation == "unspecified" || presentation == "unspecified"))
                {
                    // Known aliases and legacy chapter-scoped handles can turn
                    // multiple generated entries into the same established person.
                    // Descriptive differences do not create a new identity.
                    profiles[profiles.IndexOf(previous)] = new CharacterProfile
                    {
                        Id = previous.Id, DisplayName = previous.DisplayName,
                        Aliases = NormalizeValues(previous.Aliases.Concat(candidate.Aliases).ToArray()),
                        Description = previous.Description,
                        VoiceTraits = NormalizeVoiceTraits(previous.VoiceTraits.Concat(candidate.VoiceTraits).ToArray()),
                        VoicePresentation = previous.VoicePresentation == "unspecified" ? presentation : previous.VoicePresentation,
                        IsNamed = true, IsNarrator = sameKnownIdentity ? existing!.IsNarrator : previous.IsNarrator,
                        Importance = previous.Importance > importance ? previous.Importance : importance
                    };
                    continue;
                }
                if (previous.DisplayName == candidate.DisplayName.Trim() &&
                    previous.Description == candidate.Description.Trim() &&
                    previous.IsNamed == isNamed && previous.Importance == importance &&
                    previous.IsNarrator == candidate.IsNarrator &&
                    previous.VoicePresentation == presentation &&
                    previous.Aliases.SequenceEqual(NormalizeValues(candidate.Aliases)) &&
                    previous.VoiceTraits.SequenceEqual(NormalizeVoiceTraits(candidate.VoiceTraits)))
                {
                    continue;
                }
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
                    VoicePresentation = presentation,
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
