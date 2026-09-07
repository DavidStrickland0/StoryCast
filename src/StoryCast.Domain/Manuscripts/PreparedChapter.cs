namespace StoryCast.Domain.Manuscripts;

/// <summary>
/// Represents chapter text deterministically prepared for speech production.
/// </summary>
public sealed class PreparedChapter
{
    /// <summary>
    /// Gets the stable chapter identifier.
    /// </summary>
    public required string ChapterId { get; init; }

    /// <summary>
    /// Gets the absolute source path.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Gets the SHA-256 hash of the unchanged source text.
    /// </summary>
    public required string SourceSha256 { get; init; }

    /// <summary>
    /// Gets the version of the deterministic preparation rules.
    /// </summary>
    public required string PreparationVersion { get; init; }

    /// <summary>
    /// Gets the text that will be attributed to speakers and synthesized.
    /// </summary>
    public required string SpokenText { get; init; }
}
