namespace StoryCast.Domain.Production;

/// <summary>
/// Describes one isolated audiobook production execution.
/// </summary>
public sealed class ProductionRun
{
    /// <summary>
    /// Gets the unique production-run identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the stable identifier of the book being produced.
    /// </summary>
    public required string BookId { get; init; }

    /// <summary>
    /// Gets the UTC time at which the run was created.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Gets the absolute output directory reserved for this run.
    /// </summary>
    public required string OutputPath { get; init; }
}
