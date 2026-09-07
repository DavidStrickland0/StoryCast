using StoryCast.Domain.Books;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>
/// Loads and saves persistent casting plans.
/// </summary>
public interface ICastingPlanStore
{
    /// <summary>
    /// Loads a casting plan when one exists.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>
    /// The stored plan, or <see langword="null"/> when the book has not
    /// been cast.
    /// </returns>
    Task<CastingPlan?> LoadAsync(
        BookProject book,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically saves a casting plan.
    /// </summary>
    /// <param name="book">The owning book project.</param>
    /// <param name="plan">The casting plan to save.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    Task SaveAsync(
        BookProject book,
        CastingPlan plan,
        CancellationToken cancellationToken = default);
}
