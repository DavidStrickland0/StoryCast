namespace StoryCast.Application.Attribution;

/// <summary>A book's explicit convention for delimiting speech and optionally identifying its speaker.</summary>
public sealed record DialogueConvention
{
    /// <summary>Gets the opening delimiter.</summary>
    public required char Open { get; init; }
    /// <summary>Gets the closing delimiter.</summary>
    public required char Close { get; init; }
    /// <summary>Gets a fixed registry identity, or null to infer the speaker from context.</summary>
    public string? SpeakerId { get; init; }
    /// <summary>Gets whether an explicit name or name (origin) label overrides the default speaker.</summary>
    public bool SpeakerLabelsOverride { get; init; }
    /// <summary>Gets explicit label-to-canonical-ID overrides for duplicate or aliased identities.</summary>
    public IReadOnlyDictionary<string, string> SpeakerIdsByLabel { get; init; } = new Dictionary<string, string>();
}
