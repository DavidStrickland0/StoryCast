namespace StoryCast.Domain.Manuscripts;

/// <summary>
/// Represents one source chapter discovered in a manuscript.
/// </summary>
public sealed class ManuscriptChapter
{
    /// <summary>
    /// Gets the stable chapter identifier assigned from source order.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the chapter's zero-based position in the manuscript.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the original chapter filename.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets the absolute path from which the chapter was loaded.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Gets the chapter's source format.
    /// </summary>
    public required ManuscriptFormat Format { get; init; }

    /// <summary>
    /// Gets the complete, unchanged source text.
    /// </summary>
    public required string RawText { get; init; }
}
