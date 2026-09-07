namespace StoryCast.Domain.Production;

/// <summary>
/// Contains one verified, persistent chapter production script.
/// </summary>
public sealed class ChapterProductionArtifact
{
    /// <summary>
    /// Gets the artifact schema version.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the stable book identifier.
    /// </summary>
    public required string BookId { get; init; }

    /// <summary>
    /// Gets the SHA-256 hash of the original source chapter.
    /// </summary>
    public required string SourceSha256 { get; init; }

    /// <summary>
    /// Gets the deterministic text-preparation version.
    /// </summary>
    public required string PreparationVersion { get; init; }

    /// <summary>
    /// Gets the fully attributed and exact-text-validated script.
    /// </summary>
    public required ChapterProductionScript Script { get; init; }
}
