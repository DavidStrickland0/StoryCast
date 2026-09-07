namespace StoryCast.Application.Characters;

/// <summary>
/// Describes progress for one chapter during character discovery.
/// </summary>
public sealed class CharacterDiscoveryProgress
{
    /// <summary>
    /// Gets the zero-based chapter position.
    /// </summary>
    public required int ChapterIndex { get; init; }

    /// <summary>
    /// Gets the total number of chapters in the book.
    /// </summary>
    public required int ChapterCount { get; init; }

    /// <summary>
    /// Gets the stable chapter identifier.
    /// </summary>
    public required string ChapterId { get; init; }

    /// <summary>
    /// Gets the current progress status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the number of characters returned for this chapter.
    /// </summary>
    public required int DiscoveredCharacters { get; init; }

    /// <summary>
    /// Gets the total number of characters currently in the registry.
    /// </summary>
    public required int RegistryCharacters { get; init; }

    /// <summary>
    /// Gets the elapsed processing time for this chapter.
    /// </summary>
    public required TimeSpan Elapsed { get; init; }
}