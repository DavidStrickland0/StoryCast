using StoryCast.Domain.Characters;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Reconciles chapter character discoveries into a persistent registry.
/// </summary>
public sealed class CharacterRegistryMerger
{
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Adds or enriches character identities using one chapter's discoveries.
    /// </summary>
    /// <param name="registry">
    /// The existing registry, or <see langword="null"/> for the first chapter.
    /// </param>
    /// <param name="bookId">The stable book identifier.</param>
    /// <param name="chapterId">The processed chapter identifier.</param>
    /// <param name="sourceSha256">
    /// The SHA-256 hash of the processed source chapter.
    /// </param>
    /// <param name="discoveries">
    /// The validated character discoveries for the chapter.
    /// </param>
    /// <returns>The reconciled registry.</returns>
    public CharacterRegistry Merge(
        CharacterRegistry? registry,
        string bookId,
        string chapterId,
        string sourceSha256,
        IReadOnlyList<CharacterProfile> discoveries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(discoveries);

        if (registry is not null &&
            !string.Equals(
                registry.BookId,
                bookId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Character registry belongs to book " +
                $"'{registry.BookId}', not '{bookId}'.");
        }

        var characters = registry?.Characters.ToList() ?? [];

        foreach (var discovery in discoveries)
        {
            var matchIndex = FindMatchingCharacter(
                characters,
                discovery);

            if (matchIndex < 0)
            {
                characters.Add(NormalizeNewCharacter(discovery));
                continue;
            }

            characters[matchIndex] = MergeCharacter(
                characters[matchIndex],
                discovery);
        }

        var processedChapterHashes =
            registry?.ProcessedChapterHashes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        processedChapterHashes[chapterId] = sourceSha256;

        return new CharacterRegistry
        {
            SchemaVersion = CurrentSchemaVersion,
            BookId = bookId,
            Characters = characters
                .OrderBy(
                    character => character.Id,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProcessedChapterHashes = processedChapterHashes
        };
    }

    private static int FindMatchingCharacter(
        IReadOnlyList<CharacterProfile> existingCharacters,
        CharacterProfile discovery)
    {
        for (var index = 0;
             index < existingCharacters.Count;
             index++)
        {
            if (string.Equals(
                    existingCharacters[index].Id,
                    discovery.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        var discoveryNames = GetIdentityNames(discovery);
        var matchingIndexes = new List<int>();

        for (var index = 0;
             index < existingCharacters.Count;
             index++)
        {
            var existingNames = GetIdentityNames(
                existingCharacters[index]);

            if (discoveryNames.Overlaps(existingNames))
            {
                matchingIndexes.Add(index);
            }
        }

        if (matchingIndexes.Count > 1)
        {
            var matches = matchingIndexes.Select(
                index => existingCharacters[index].Id);

            throw new InvalidDataException(
                $"Character discovery '{discovery.Id}' ambiguously matches: " +
                string.Join(", ", matches));
        }

        return matchingIndexes.Count == 1
            ? matchingIndexes[0]
            : -1;
    }

    private static HashSet<string> GetIdentityNames(
        CharacterProfile character)
    {
        var values = character.Aliases
            .Append(character.DisplayName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeIdentityName);

        return new HashSet<string>(
            values,
            StringComparer.Ordinal);
    }

    private static string NormalizeIdentityName(string value)
    {
        return string.Join(
            ' ',
            value
                .Trim()
                .ToLowerInvariant()
                .Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries));
    }

    private static CharacterProfile MergeCharacter(
        CharacterProfile existing,
        CharacterProfile discovery)
    {
        var aliases = existing.Aliases
            .Concat(discovery.Aliases)
            .Concat(
                string.Equals(
                    existing.DisplayName,
                    discovery.DisplayName,
                    StringComparison.OrdinalIgnoreCase)
                    ? []
                    : [discovery.DisplayName])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var voiceTraits = existing.VoiceTraits
            .Concat(discovery.VoiceTraits)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !IsPresentationToken(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CharacterProfile
        {
            Id = existing.Id,
            DisplayName = existing.DisplayName,
            Aliases = aliases,
            Description = string.IsNullOrWhiteSpace(existing.Description)
                ? discovery.Description.Trim()
                : existing.Description,
            VoiceTraits = voiceTraits,
            VoicePresentation = MergePresentation(
                existing.VoicePresentation,
                discovery.VoicePresentation,
                existing.Id),
            Importance = MaxImportance(
                existing.Importance,
                discovery.Importance),
            IsNarrator =
                existing.IsNarrator || discovery.IsNarrator
        };
    }

    private static CharacterProfile NormalizeNewCharacter(
        CharacterProfile character)
    {
        return new CharacterProfile
        {
            Id = character.Id,
            DisplayName = character.DisplayName.Trim(),
            Aliases = character.Aliases
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Description = character.Description.Trim(),
            VoiceTraits = character.VoiceTraits
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Where(value => !IsPresentationToken(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            VoicePresentation = character.VoicePresentation,
            Importance = character.Importance,
            IsNarrator = character.IsNarrator
        };
    }

    private static bool IsPresentationToken(string value)
    {
        return string.Equals(
                value,
                "male",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "female",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "unspecified",
                StringComparison.OrdinalIgnoreCase);
    }
    private static string MergePresentation(
        string existing,
        string discovered,
        string characterId)
    {
        if (string.Equals(
                existing,
                "unspecified",
                StringComparison.OrdinalIgnoreCase))
        {
            return discovered;
        }

        if (string.Equals(
                discovered,
                "unspecified",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                existing,
                discovered,
                StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        throw new InvalidDataException(
            $"Character '{characterId}' has conflicting voice " +
            $"presentations: '{existing}' and '{discovered}'.");
    }

    private static CharacterImportance MaxImportance(
        CharacterImportance first,
        CharacterImportance second)
    {
        return (CharacterImportance)Math.Max(
            (int)first,
            (int)second);
    }
}
