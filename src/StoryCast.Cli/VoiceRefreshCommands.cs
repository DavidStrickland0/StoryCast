using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class VoiceRefreshCommands
{
    /// <summary>
    /// Rebuilds, analyzes, and verifies the derived voice library.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>
    /// Zero when the refreshed library is ready; otherwise, a nonzero
    /// exit code.
    /// </returns>
    public static async Task<int> RefreshAsync(string[] args)
    {
        string? stagingPath = null;

        try
        {
            var samplesPath = Path.GetFullPath(
                GetOptionValue(args, "--samples") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "samples"));

            var libraryPath = Path.GetFullPath(
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices"));

            var verificationModel =
                GetOptionValue(args, "--model") ??
                "small.en";

            var workerImage =
                GetOptionValue(args, "--worker-image") ??
                "storycast-worker:dev";

            ValidatePaths(samplesPath, libraryPath);

            var libraryParent =
                Path.GetDirectoryName(libraryPath) ??
                throw new InvalidDataException(
                    $"Voice library has no parent directory: {libraryPath}");

            Directory.CreateDirectory(libraryParent);

            stagingPath = Path.Combine(
                libraryParent,
                $".voices-refresh-{Guid.NewGuid():N}");

            Console.WriteLine($"Samples:       {samplesPath}");
            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Model:         {verificationModel}");
            Console.WriteLine($"Worker image:  {workerImage}");
            Console.WriteLine();

            var imported = await BuildLibraryAsync(
                samplesPath,
                stagingPath);

            Console.WriteLine();
            Console.WriteLine($"Imported: {imported}");
            Console.WriteLine();

            Console.WriteLine("===== Voice analysis =====");

            var analysisExitCode =
                await VoiceCommands.AnalyzeAsync(
                    [
                        "--library",
                        stagingPath
                    ]);

            if (analysisExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"Voice refresh failed during analysis with exit code " +
                    $"{analysisExitCode}.");

                return analysisExitCode;
            }

            Console.WriteLine();
            Console.WriteLine("===== Voice verification =====");

            var verificationExitCode =
                await VoiceVerificationCommands.VerifyAsync(
                    [
                        "--library",
                        stagingPath,
                        "--model",
                        verificationModel,
                        "--worker-image",
                        workerImage
                    ]);

            if (verificationExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"Voice refresh failed during verification with exit " +
                    $"code {verificationExitCode}.");

                return verificationExitCode;
            }

            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(
                    libraryPath,
                    recursive: true);
            }

            Directory.Move(
                stagingPath,
                libraryPath);

            stagingPath = null;

            Console.WriteLine();
            Console.WriteLine("Voice library refresh complete.");
            Console.WriteLine($"Voices:  {imported}");
            Console.WriteLine($"Library: {libraryPath}");

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException or
            ArgumentException)
        {
            Console.Error.WriteLine(
                $"Voice refresh failed: {exception.Message}");

            return 1;
        }
        finally
        {
            if (stagingPath is not null &&
                Directory.Exists(stagingPath))
            {
                Directory.Delete(
                    stagingPath,
                    recursive: true);
            }
        }
    }

    /// <summary>
    /// Writes detailed help for voice-library refresh.
    /// </summary>
    public static void WriteHelp()
    {
        Console.WriteLine(
            """
            StoryCast voice-library refresh

            Usage:
              storycast voices refresh [options]

            Behavior:
              Rebuilds the derived voice library from source entries in
              .\samples. Each voice receives a neutral, deterministic ID
              derived from the SHA-256 hash of its WAV sample.

              The rebuilt library is analyzed and verified before it
              replaces the current .\voices directory.

            Options:
              --samples <path>
                  Source voice-sample directory. Defaults to .\samples.

              --library <path>
                  Derived voice-library directory. Defaults to .\voices.

              --model <model>
                  Whisper verification model. Defaults to small.en.

              --worker-image <image>
                  Docker worker image. Defaults to storycast-worker:dev.

              --help, -h
                  Display this help.
            """);
    }

    private static async Task<int> BuildLibraryAsync(
        string samplesPath,
        string stagingPath)
    {
        Directory.CreateDirectory(stagingPath);

        var manifests = Directory
            .EnumerateFiles(
                samplesPath,
                "voice.json",
                SearchOption.AllDirectories)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (manifests.Length == 0)
        {
            throw new InvalidDataException(
                $"No source voice manifests were found: {samplesPath}");
        }

        var voiceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var imported = 0;

        foreach (var manifestPath in manifests)
        {
            var manifest = await LoadManifestAsync(
                manifestPath);

            var sampleName =
                manifest["sample"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(sampleName))
            {
                throw new InvalidDataException(
                    $"Voice manifest is missing its sample filename: " +
                    manifestPath);
            }

            var sourceDirectory =
                Path.GetDirectoryName(manifestPath) ??
                throw new InvalidDataException(
                    $"Voice manifest has no parent directory: " +
                    manifestPath);

            var samplePath = Path.GetFullPath(
                Path.Combine(
                    sourceDirectory,
                    sampleName));

            ValidateContainedPath(
                sourceDirectory,
                samplePath,
                manifestPath);

            if (!File.Exists(samplePath))
            {
                throw new FileNotFoundException(
                    $"Voice sample was not found: {samplePath}",
                    samplePath);
            }

            var voiceId = await CreateVoiceIdAsync(
                samplePath);

            if (!voiceIds.Add(voiceId))
            {
                throw new InvalidDataException(
                    $"Duplicate source audio produced voice ID " +
                    $"'{voiceId}'.");
            }

            var destinationDirectory = Path.Combine(
                stagingPath,
                voiceId);

            Directory.CreateDirectory(
                destinationDirectory);

            var destinationSamplePath = Path.Combine(
                destinationDirectory,
                "sample.wav");

            File.Copy(
                samplePath,
                destinationSamplePath,
                overwrite: false);

            manifest["id"] = voiceId;
            manifest["sample"] = "sample.wav";

            var destinationManifestPath = Path.Combine(
                destinationDirectory,
                "voice.json");

            await File.WriteAllTextAsync(
                destinationManifestPath,
                manifest.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }) +
                Environment.NewLine);

            imported++;

            Console.WriteLine(
                $"[{imported,2}/{manifests.Length}] " +
                $"{Path.GetFileName(sourceDirectory)} -> {voiceId}");
        }

        return imported;
    }

    private static async Task<JsonObject> LoadManifestAsync(
        string manifestPath)
    {
        await using var stream = File.OpenRead(
            manifestPath);

        var node = await JsonNode.ParseAsync(
            stream);

        return node as JsonObject ??
            throw new InvalidDataException(
                $"Voice manifest must contain a JSON object: " +
                manifestPath);
    }

    private static async Task<string> CreateVoiceIdAsync(
        string samplePath)
    {
        await using var stream = File.OpenRead(
            samplePath);

        var hash = await SHA256.HashDataAsync(
            stream);

        return $"voice-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static void ValidatePaths(
        string samplesPath,
        string libraryPath)
    {
        if (!Directory.Exists(samplesPath))
        {
            throw new DirectoryNotFoundException(
                $"Source sample directory was not found: {samplesPath}");
        }

        if (string.Equals(
                samplesPath,
                libraryPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The sample source and derived voice library must be " +
                "different directories.");
        }

        var relativeLibraryPath = Path.GetRelativePath(
            samplesPath,
            libraryPath);

        var relativeSamplesPath = Path.GetRelativePath(
            libraryPath,
            samplesPath);

        if (!IsOutside(relativeLibraryPath) ||
            !IsOutside(relativeSamplesPath))
        {
            throw new ArgumentException(
                "The sample source and voice library cannot contain " +
                "one another.");
        }
    }

    private static bool IsOutside(string relativePath)
    {
        return relativePath == ".." ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static void ValidateContainedPath(
        string directory,
        string path,
        string manifestPath)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(directory),
            path);

        if (IsOutside(relativePath))
        {
            throw new InvalidDataException(
                $"Voice sample must remain inside its source directory: " +
                manifestPath);
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