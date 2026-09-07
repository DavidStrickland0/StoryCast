namespace StoryCast.Domain.Voices;

/// <summary>
/// Describes a reusable voice available for audiobook casting.
/// </summary>
public sealed class VoiceProfile
{
    /// <summary>
    /// Gets the stable identifier for the voice.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the path to the reference audio sample.
    /// </summary>
    public required string SamplePath { get; init; }

    /// <summary>
    /// Gets the primary language spoken by the voice.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Gets the accent associated with the voice.
    /// </summary>
    public string Accent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the voice's apparent age range.
    /// </summary>
    public string ApparentAge { get; init; } = string.Empty;

    /// <summary>
    /// Gets the voice's perceived presentation.
    /// </summary>
    public string Presentation { get; init; } = string.Empty;

    /// <summary>
    /// Gets descriptive qualities used during automatic casting.
    /// </summary>
    public IReadOnlyList<string> Qualities { get; init; } = [];

    /// <summary>
    /// Gets the kinds of roles for which the voice is suitable.
    /// </summary>
    public IReadOnlyList<string> SuitableRoles { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the voice may be selected as a narrator.
    /// </summary>
    public bool NarratorSuitable { get; init; }
}
