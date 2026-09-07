using StoryCast.Domain.Books;

namespace StoryCast.Application.Books;

/// <summary>
/// Loads and validates configured audiobook projects.
/// </summary>
public interface IBookProjectLoader
{
    /// <summary>
    /// Loads a book project from a directory or book.json path.
    /// </summary>
    /// <param name="projectPath">
    /// The project directory or explicit book.json path.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The validated book project.</returns>
    Task<BookProject> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default);
}
