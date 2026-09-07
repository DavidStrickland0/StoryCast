using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;

namespace StoryCast.Application.Characters;

/// <summary>
/// Loads and saves the persistent character registry for a book.
/// </summary>
public interface ICharacterRegistryStore
{
    /// <summary>
    /// Loads the registry when one exists.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>
    /// The stored registry, or <see langword="null"/> when character discovery
    /// has not yet been performed.
    /// </returns>
    Task<CharacterRegistry?> LoadAsync(
        BookProject book,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically saves the character registry.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="registry">The registry to save.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    Task SaveAsync(
        BookProject book,
        CharacterRegistry registry,
        CancellationToken cancellationToken = default);
}
