using StoryCast.Domain.Manuscripts;

namespace StoryCast.Domain.Books;

/// <summary>
/// Represents a configured audiobook production project.
/// </summary>
public sealed class BookProject
{
    /// <summary>
    /// Gets the supported manifest schema version.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the stable book identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the book title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the author name.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Gets the primary language code.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the absolute project directory.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Gets the explicitly ordered manuscript.
    /// </summary>
    public required Manuscript Manuscript { get; init; }
}
