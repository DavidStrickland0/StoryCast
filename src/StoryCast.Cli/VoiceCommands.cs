using System.Text.Json;
using StoryCast.Application.Voices;
using StoryCast.Infrastructure.Voices;

internal static class VoiceCommands
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

    public static async Task<int> EnrichAsync(string[] args)
    {
        try
        {
            var libraryPath =
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices");

            var enricher =
                new FileSystemVoiceLibraryEnricher();

            Console.WriteLine(
                $"Voice library: {Path.GetFullPath(libraryPath)}");

            Console.WriteLine("Enriching voice metadata...");

            var result = await enricher.EnrichAsync(
                libraryPath);

            Console.WriteLine();
            Console.WriteLine("Voice enrichment complete.");
            Console.WriteLine(
                $"Updated:      {result.UpdatedVoices}");
            Console.WriteLine(
                $"Unchanged:    {result.UnchangedVoices}");
            Console.WriteLine(
                $"Unrecognized: {result.UnrecognizedVoices}");

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            Console.Error.WriteLine(
                $"Voice enrichment failed: {exception.Message}");

            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> AnalyzeAsync(string[] args)
    {
        try
        {
            var libraryPath =
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices");

            libraryPath = Path.GetFullPath(libraryPath);

            var outputPath =
                GetOptionValue(args, "--output") ??
                Path.Combine(
                    libraryPath,
                    "voice-analysis.json");

            outputPath = Path.GetFullPath(outputPath);

            IVoiceLibrary voiceLibrary =
                new FileSystemVoiceLibrary();

            IVoiceSampleAnalyzer analyzer =
                new FfmpegVoiceSampleAnalyzer();

            var voices = await voiceLibrary.LoadAsync(
                libraryPath);

            if (voices.Count == 0)
            {
                Console.Error.WriteLine(
                    "The voice library contains no voices.");

                return 1;
            }

            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Voices:        {voices.Count}");
            Console.WriteLine("Analyzing samples...");
            Console.WriteLine();

            var analyses =
                new List<VoiceSampleAnalysis>(voices.Count);

            foreach (var voice in voices.OrderBy(
                         voice => voice.Id,
                         StringComparer.OrdinalIgnoreCase))
            {
                var analysis = await analyzer.AnalyzeAsync(
                    voice);

                analyses.Add(analysis);

                Console.WriteLine(
                    $"[{analyses.Count,2}/{voices.Count}] " +
                    $"{voice.Id}  " +
                    $"{analysis.DurationSeconds:0.00}s  " +
                    $"{analysis.MeanVolumeDb:0.0} dB mean  " +
                    $"{analysis.PeakVolumeDb:0.0} dB peak");
            }

            var report = new VoiceLibraryAnalysisReport
            {
                SchemaVersion = 1,
                GeneratedUtc = DateTimeOffset.UtcNow,
                Voices = analyses
            };

            await SaveReportAsync(
                outputPath,
                report);

            var clippingRisks = analyses.Count(
                analysis => analysis.HasClippingRisk);

            Console.WriteLine();
            Console.WriteLine("Voice analysis complete.");
            Console.WriteLine($"Analyzed:      {analyses.Count}");
            Console.WriteLine($"Clipping risk: {clippingRisks}");
            Console.WriteLine($"Report:        {outputPath}");

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Voice analysis failed: {exception.Message}");

            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task SaveReportAsync(
        string outputPath,
        VoiceLibraryAnalysisReport report)
    {
        var parentDirectory =
            Path.GetDirectoryName(outputPath) ??
            throw new InvalidDataException(
                $"Analysis report has no parent directory: " +
                outputPath);

        Directory.CreateDirectory(parentDirectory);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".voice-analysis-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    report,
                    SerializerOptions);

                await stream.WriteAsync(
                    Environment.NewLine
                        .Select(character => (byte)character)
                        .ToArray());

                await stream.FlushAsync();
            }

            if (File.Exists(outputPath))
            {
                File.Replace(
                    temporaryPath,
                    outputPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    outputPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? GetOptionValue(
        IReadOnlyList<string> args,
        string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(
                    args[index],
                    option,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count ||
                args[index + 1].StartsWith(
                    "--",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Option {option} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }
}
