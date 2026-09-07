namespace StoryCast.Domain.Manuscripts;

/// <summary>
/// Represents a manuscript loaded from one file or a chapter directory.
/// </summary>
public sealed class Manuscript
{
    /// <summary>
    /// Gets the absolute source path supplied by the user.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Gets the chapters in deterministic reading order.
    /// </summary>
    public required IReadOnlyList<ManuscriptChapter> Chapters { get; init; }
}
