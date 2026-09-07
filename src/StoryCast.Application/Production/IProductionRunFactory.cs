using StoryCast.Domain.Books;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Production;

/// <summary>
/// Creates isolated output locations for audiobook production runs.
/// </summary>
public interface IProductionRunFactory
{
    /// <summary>
    /// Creates and reserves a new production run.
    /// </summary>
    /// <param name="book">The book being produced.</param>
    /// <param name="requestedOutputPath">
    /// An explicit output directory, or <see langword="null"/> to create a
    /// unique directory beneath the book's output directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The newly created production run.</returns>
    Task<ProductionRun> CreateAsync(
        BookProject book,
        string? requestedOutputPath = null,
        CancellationToken cancellationToken = default);
}
