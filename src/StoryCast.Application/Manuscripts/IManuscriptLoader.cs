using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Manuscripts;

/// <summary>
/// Loads manuscripts from supported source files.
/// </summary>
public interface IManuscriptLoader
{
    /// <summary>
    /// Loads a single manuscript file or a directory of chapter files.
    /// </summary>
    /// <param name="sourcePath">
    /// A supported file or a directory containing supported files.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The loaded manuscript.</returns>
    Task<Manuscript> LoadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
