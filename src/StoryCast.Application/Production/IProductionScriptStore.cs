using StoryCast.Domain.Books;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Production;

/// <summary>
/// Loads and saves verified chapter production scripts.
/// </summary>
public interface IProductionScriptStore
{
    /// <summary>
    /// Loads a stored chapter script when one exists.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="chapterId">The stable chapter identifier.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>
    /// The stored artifact, or <see langword="null"/> when none exists.
    /// </returns>
    Task<ChapterProductionArtifact?> LoadAsync(
        BookProject book,
        string chapterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically saves a verified chapter production script.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="artifact">The artifact to save.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    Task SaveAsync(
        BookProject book,
        ChapterProductionArtifact artifact,
        CancellationToken cancellationToken = default);
}
