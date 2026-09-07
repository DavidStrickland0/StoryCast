namespace StoryCast.Domain.Characters;

/// <summary>
/// Contains the persistent character identities established for one book.
/// </summary>
public sealed class CharacterRegistry
{
    /// <summary>
    /// Gets the character-registry schema version.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the stable book identifier.
    /// </summary>
    public required string BookId { get; init; }

    /// <summary>
    /// Gets the established character profiles.
    /// </summary>
    public required IReadOnlyList<CharacterProfile> Characters { get; init; }

    /// <summary>
    /// Gets the source hash processed for each chapter identifier.
    /// </summary>
    public required IReadOnlyDictionary<string, string>
        ProcessedChapterHashes
    { get; init; }
}
