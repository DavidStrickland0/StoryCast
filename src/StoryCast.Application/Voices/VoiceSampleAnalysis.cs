namespace StoryCast.Application.Voices;

/// <summary>
/// Contains objective measurements for one voice-reference sample.
/// </summary>
public sealed class VoiceSampleAnalysis
{
    /// <summary>
    /// Gets the analyzed voice identifier.
    /// </summary>
    public required string VoiceId { get; init; }

    /// <summary>
    /// Gets the sample duration in seconds.
    /// </summary>
    public required decimal DurationSeconds { get; init; }

    /// <summary>
    /// Gets the audio codec name.
    /// </summary>
    public required string CodecName { get; init; }

    /// <summary>
    /// Gets the sample rate in hertz.
    /// </summary>
    public required int SampleRate { get; init; }

    /// <summary>
    /// Gets the number of audio channels.
    /// </summary>
    public required int Channels { get; init; }

    /// <summary>
    /// Gets the mean sample volume in decibels.
    /// </summary>
    public required decimal MeanVolumeDb { get; init; }

    /// <summary>
    /// Gets the maximum sample volume in decibels.
    /// </summary>
    public required decimal PeakVolumeDb { get; init; }

    /// <summary>
    /// Gets a value indicating whether the sample reaches digital maximum.
    /// </summary>
    public bool HasClippingRisk => PeakVolumeDb >= 0;
}
