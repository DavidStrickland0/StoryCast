using System.Diagnostics;

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

            var python =
                GetOptionValue(args, "--python") ??
                "/home/user/.venvs/storycast/bin/python";

            var cudaLibraryPath =
                GetOptionValue(
                    args,
                    "--cuda-library-path") ??
                string.Join(
                    ':',
                    "/home/user/.venvs/storycast/lib/python3.12/" +
                    "site-packages/nvidia/cublas/lib",
                    "/home/user/.venvs/storycast/lib/python3.12/" +
                    "site-packages/nvidia/cudnn/lib");

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

            var wslLibraryPath = await ConvertToWslPathAsync(
                libraryPath);

            var wslScriptPath = await ConvertToWslPathAsync(
                scriptPath);

            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Model:         {model}");
            Console.WriteLine("Starting WSL voice verification...");
            Console.WriteLine();

            var exitCode = await RunVerifierAsync(
                python,
                wslScriptPath,
                wslLibraryPath,
                model,
                cudaLibraryPath);

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

    private static async Task<string> ConvertToWslPathAsync(
        string windowsPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("wslpath");
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(windowsPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Unable to start WSL.");
        }

        var outputTask =
            process.StandardOutput.ReadToEndAsync();

        var errorTask =
            process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();

        if (process.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"Unable to convert Windows path for WSL: " +
                $"{windowsPath}. {error}");
        }

        return output;
    }

    private static async Task<int> RunVerifierAsync(
        string python,
        string scriptPath,
        string libraryPath,
        string model,
        string cudaLibraryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("env");
        startInfo.ArgumentList.Add(
            $"LD_LIBRARY_PATH={cudaLibraryPath}");
        startInfo.ArgumentList.Add(python);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(libraryPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(model);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Unable to start the WSL voice verifier.");
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
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
