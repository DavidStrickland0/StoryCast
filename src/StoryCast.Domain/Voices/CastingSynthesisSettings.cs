namespace StoryCast.Domain.Voices;

/// <summary>
/// Defines Chatterbox synthesis behavior for one audiobook role.
/// </summary>
public sealed class CastingSynthesisSettings
{
    /// <summary>
    /// Gets the emotional intensity applied during synthesis.
    /// </summary>
    public decimal Exaggeration { get; init; } = 0.65m;

    /// <summary>
    /// Gets the classifier-free guidance weight.
    /// </summary>
    public decimal CfgWeight { get; init; } = 0.5m;

    /// <summary>
    /// Gets the synthesis sampling temperature.
    /// </summary>
    public decimal Temperature { get; init; } = 0.7m;
}