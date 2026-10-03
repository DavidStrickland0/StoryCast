using System.Text.Json;
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

            var workerImage =
                GetOptionValue(args, "--worker-image") ??
                "storycast-worker:dev";

            if (!Directory.Exists(libraryPath))
            {
                throw new DirectoryNotFoundException(
                    $"Voice library was not found: {libraryPath}");
            }

            var runtime = new DockerWorkerRuntime(
                null,
                libraryPath,
                workerImage,
                libraryReadOnly: false);

            var arguments = new List<string>
            {
                runtime.LibraryPath
            };

            var outputValue = GetOptionValue(args, "--output");
            if (outputValue is not null)
            {
                var outputPath = Path.GetFullPath(outputValue);
                var relativeOutput = Path.GetRelativePath(
                    libraryPath,
                    outputPath);

                if (Path.IsPathRooted(relativeOutput) ||
                    relativeOutput == ".." ||
                    relativeOutput.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The analysis output must be inside the voice library.");
                }

                arguments.Add("--output");
                arguments.Add(
                    $"{runtime.LibraryPath}/" +
                    relativeOutput.Replace('\\', '/'));
            }

            Console.WriteLine(
                "Starting Docker voice analysis...");
            Console.WriteLine();

            return await runtime.RunAsync(
                "analyze_voice_library.py",
                arguments,
                useGpu: false);
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
