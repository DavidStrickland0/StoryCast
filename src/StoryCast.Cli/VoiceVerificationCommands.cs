internal static class VoiceVerificationCommands
{
    public static async Task<int> VerifyAsync(string[] args)
    {
        try
        {
            var libraryPath = Path.GetFullPath(
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices"));

            var scriptPath = Path.GetFullPath(
                GetOptionValue(args, "--script") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "tools",
                    "verify_voice_library.py"));

            var model =
                GetOptionValue(args, "--model") ??
                "small.en";

            var workerImage =
                GetOptionValue(
                    args,
                    "--worker-image") ??
                "storycast-worker:dev";

            if (!Directory.Exists(libraryPath))
            {
                throw new DirectoryNotFoundException(
                    $"Voice library was not found: {libraryPath}");
            }

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException(
                    $"Voice verifier was not found: {scriptPath}",
                    scriptPath);
            }

            var runtime = new DockerWorkerRuntime(
                null,
                libraryPath,
                workerImage,
                libraryReadOnly: false);

            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Model:         {model}");
            Console.WriteLine(
                "Starting Docker voice verification...");
            Console.WriteLine();

            var exitCode = await RunVerifierAsync(
                runtime,
                Path.GetFileName(scriptPath),
                model);

            Console.WriteLine();

            if (exitCode == 0)
            {
                Console.WriteLine(
                    "Voice verification completed successfully.");
            }
            else
            {
                Console.Error.WriteLine(
                    $"Voice verification failed with exit code " +
                    $"{exitCode}.");
            }

            return exitCode;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Voice verification failed: {exception.Message}");

            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Task<int> RunVerifierAsync(
        DockerWorkerRuntime runtime,
        string scriptName,
        string model)
    {
        return runtime.RunAsync(
            scriptName,
            [
                runtime.LibraryPath,
                "--model",
                model
            ]);
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
