namespace StoryCast.Domain.Voices;

/// <summary>Actual attributed speech use within one chapter.</summary>
public sealed class ChapterVoiceUsage
{
    /// <summary>Zero-based manuscript position.</summary>
    public required int ChapterIndex { get; init; }
    /// <summary>Source hash used for attribution.</summary>
    public required string SourceSha256 { get; init; }
    /// <summary>Nonempty speech segment counts by voice.</summary>
    public required IReadOnlyDictionary<string, int> VoiceUseCounts { get; init; }
}
