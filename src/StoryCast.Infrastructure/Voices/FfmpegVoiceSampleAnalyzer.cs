using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StoryCast.Application.Voices;
using StoryCast.Domain.Voices;

namespace StoryCast.Infrastructure.Voices;

/// <summary>
/// Uses FFprobe and FFmpeg to measure voice-reference audio.
/// </summary>
public sealed partial class FfmpegVoiceSampleAnalyzer
    : IVoiceSampleAnalyzer
{
    private readonly string ffprobeExecutable;
    private readonly string ffmpegExecutable;

    /// <summary>
    /// Initializes an FFmpeg-based voice-sample analyzer.
    /// </summary>
    /// <param name="ffprobeExecutable">
    /// The FFprobe executable name or path.
    /// </param>
    /// <param name="ffmpegExecutable">
    /// The FFmpeg executable name or path.
    /// </param>
    public FfmpegVoiceSampleAnalyzer(
        string ffprobeExecutable = "ffprobe",
        string ffmpegExecutable = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            ffprobeExecutable);

        ArgumentException.ThrowIfNullOrWhiteSpace(
            ffmpegExecutable);

        this.ffprobeExecutable = ffprobeExecutable;
        this.ffmpegExecutable = ffmpegExecutable;
    }

    /// <inheritdoc />
    public async Task<VoiceSampleAnalysis> AnalyzeAsync(
        VoiceProfile voice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voice);

        if (!File.Exists(voice.SamplePath))
        {
            throw new FileNotFoundException(
                $"Voice sample was not found: {voice.SamplePath}",
                voice.SamplePath);
        }

        var probeOutput = await RunAsync(
            ffprobeExecutable,
            [
                "-v",
                "error",
                "-select_streams",
                "a:0",
                "-show_entries",
                "stream=codec_name,sample_rate,channels,duration",
                "-show_entries",
                "format=duration",
                "-of",
                "json",
                voice.SamplePath
            ],
            cancellationToken);

        var volumeOutput = await RunAsync(
            ffmpegExecutable,
            [
                "-hide_banner",
                "-nostats",
                "-i",
                voice.SamplePath,
                "-af",
                "volumedetect",
                "-f",
                "null",
                OperatingSystem.IsWindows() ? "NUL" : "/dev/null"
            ],
            cancellationToken);

        var probe = ParseProbe(
            probeOutput.StandardOutput,
            voice.SamplePath);

        return new VoiceSampleAnalysis
        {
            VoiceId = voice.Id,
            DurationSeconds = probe.DurationSeconds,
            CodecName = probe.CodecName,
            SampleRate = probe.SampleRate,
            Channels = probe.Channels,
            MeanVolumeDb = ParseVolume(
                MeanVolumeRegex(),
                volumeOutput.StandardError,
                "mean",
                voice.SamplePath),
            PeakVolumeDb = ParseVolume(
                PeakVolumeRegex(),
                volumeOutput.StandardError,
                "peak",
                voice.SamplePath)
        };
    }

    private static ProbeResult ParseProbe(
        string json,
        string samplePath)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            var streams = document.RootElement.GetProperty(
                "streams");

            if (streams.GetArrayLength() == 0)
            {
                throw new InvalidDataException(
                    $"Voice sample has no audio stream: {samplePath}");
            }

            var stream = streams[0];

            var durationText =
                GetOptionalString(stream, "duration") ??
                document.RootElement
                    .GetProperty("format")
                    .GetProperty("duration")
                    .GetString();

            if (!decimal.TryParse(
                    durationText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var durationSeconds))
            {
                throw new InvalidDataException(
                    $"Voice sample has an invalid duration: {samplePath}");
            }

            var sampleRateText = stream
                .GetProperty("sample_rate")
                .GetString();

            if (!int.TryParse(
                    sampleRateText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var sampleRate))
            {
                throw new InvalidDataException(
                    $"Voice sample has an invalid sample rate: " +
                    samplePath);
            }

            return new ProbeResult(
                stream.GetProperty("codec_name").GetString() ??
                    string.Empty,
                sampleRate,
                stream.GetProperty("channels").GetInt32(),
                durationSeconds);
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            InvalidOperationException)
        {
            throw new InvalidDataException(
                $"FFprobe returned invalid data for voice sample: " +
                samplePath,
                exception);
        }
    }

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(
            propertyName,
            out var property)
            ? property.GetString()
            : null;
    }

    private static decimal ParseVolume(
        Regex regex,
        string output,
        string measurementName,
        string samplePath)
    {
        var match = regex.Match(output);

        if (!match.Success ||
            !decimal.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new InvalidDataException(
                $"FFmpeg did not report {measurementName} volume for " +
                $"voice sample: {samplePath}");
        }

        return value;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Unable to start {executable}.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Unable to start {executable}. Ensure FFmpeg is installed " +
                "and available on PATH.",
                exception);
        }

        var standardOutputTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);

        var standardErrorTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"{executable} failed with exit code " +
                $"{process.ExitCode}: {standardError.Trim()}");
        }

        return new ProcessResult(
            standardOutput,
            standardError);
    }

    [GeneratedRegex(
        @"mean_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB",
        RegexOptions.CultureInvariant)]
    private static partial Regex MeanVolumeRegex();

    [GeneratedRegex(
        @"max_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB",
        RegexOptions.CultureInvariant)]
    private static partial Regex PeakVolumeRegex();

    private sealed record ProbeResult(
        string CodecName,
        int SampleRate,
        int Channels,
        decimal DurationSeconds);

    private sealed record ProcessResult(
        string StandardOutput,
        string StandardError);
}
