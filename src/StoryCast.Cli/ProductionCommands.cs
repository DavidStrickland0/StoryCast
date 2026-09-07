using System.Diagnostics;

internal static class ProductionCommands
{
    /// <summary>
    /// Produces, verifies, assembles, and masters one chapter.
    /// </summary>
    /// <param name="args">The chapter-production arguments.</param>
    /// <returns>
    /// A task containing zero on success; otherwise, a nonzero exit code.
    /// </returns>
    public static async Task<int> ProduceChapterAsync(
        string[] args)
    {
        if (args.Length < 2 ||
            args[0].StartsWith("--", StringComparison.Ordinal) ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Chapter production requires a book directory " +
                "and chapter ID.");

            return 1;
        }

        try
        {
            var bookPath = Path.GetFullPath(
                args[0]);

            var chapterId = args[1];

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
                    "produce_chapter.py"));

            var whisperModel =
                GetOptionValue(
                    args,
                    "--whisper-model") ??
                "small.en";

            var runDirectoryValue =
                GetOptionValue(
                    args,
                    "--run-directory");

            var runDirectoryPath =
                runDirectoryValue is null
                    ? null
                    : Path.GetFullPath(
                        runDirectoryValue);

            var resumeRunValue =
                GetOptionValue(
                    args,
                    "--resume-run");

            var resumeRunPath =
                resumeRunValue is null
                    ? null
                    : Path.GetFullPath(
                        resumeRunValue);

            if (runDirectoryPath is not null &&
                resumeRunPath is not null)
            {
                throw new ArgumentException(
                    "--run-directory and --resume-run cannot " +
                    "be used together.");
            }

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

            if (!Directory.Exists(bookPath))
            {
                throw new DirectoryNotFoundException(
                    $"Book directory was not found: {bookPath}");
            }

            if (!Directory.Exists(libraryPath))
            {
                throw new DirectoryNotFoundException(
                    $"Voice library was not found: {libraryPath}");
            }

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException(
                    $"Production worker was not found: {scriptPath}",
                    scriptPath);
            }

            if (runDirectoryPath is not null &&
                Directory.Exists(runDirectoryPath))
            {
                throw new IOException(
                    $"New run directory already exists: " +
                    $"{runDirectoryPath}");
            }

            if (resumeRunPath is not null &&
                !Directory.Exists(resumeRunPath))
            {
                throw new DirectoryNotFoundException(
                    $"Resume run directory was not found: " +
                    $"{resumeRunPath}");
            }

            var wslBookPath = await ConvertToWslPathAsync(
                bookPath);

            var wslLibraryPath = await ConvertToWslPathAsync(
                libraryPath);

            var wslScriptPath = await ConvertToWslPathAsync(
                scriptPath);

            var wslRunDirectoryPath =
                runDirectoryPath is null
                    ? null
                    : await ConvertToWslPathAsync(
                        runDirectoryPath);

            var wslResumeRunPath =
                resumeRunPath is null
                    ? null
                    : await ConvertToWslPathAsync(
                        resumeRunPath);

            Console.WriteLine($"Book:          {bookPath}");
            Console.WriteLine($"Chapter:       {chapterId}");
            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Whisper:       {whisperModel}");

            if (runDirectoryPath is not null)
            {
                Console.WriteLine(
                    $"Run directory: {runDirectoryPath}");
            }

            if (resumeRunPath is not null)
            {
                Console.WriteLine(
                    $"Resume run:    {resumeRunPath}");
            }
            Console.WriteLine();
            Console.WriteLine("Starting chapter production...");
            Console.WriteLine();

            var exitCode = await RunWorkerAsync(
                python,
                wslScriptPath,
                wslBookPath,
                wslLibraryPath,
                chapterId,
                whisperModel,
                cudaLibraryPath,
                wslRunDirectoryPath,
                wslResumeRunPath);

            Console.WriteLine();

            if (exitCode == 0)
            {
                Console.WriteLine(
                    "Chapter production completed successfully.");
            }
            else
            {
                Console.Error.WriteLine(
                    $"Chapter production failed with exit code " +
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
                $"Chapter production failed: {exception.Message}");

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

    private static async Task<int> RunWorkerAsync(
        string python,
        string scriptPath,
        string bookPath,
        string libraryPath,
        string chapterId,
        string whisperModel,
        string cudaLibraryPath,
        string? runDirectoryPath,
        string? resumeRunPath)
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
        startInfo.ArgumentList.Add(
            "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True");
        startInfo.ArgumentList.Add(python);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(bookPath);
        startInfo.ArgumentList.Add(libraryPath);
        startInfo.ArgumentList.Add("--chapter");
        startInfo.ArgumentList.Add(chapterId);
        startInfo.ArgumentList.Add("--whisper-model");
        startInfo.ArgumentList.Add(whisperModel);

        if (runDirectoryPath is not null)
        {
            startInfo.ArgumentList.Add(
                "--run-directory");
            startInfo.ArgumentList.Add(
                runDirectoryPath);
        }

        if (resumeRunPath is not null)
        {
            startInfo.ArgumentList.Add(
                "--resume-run-directory");
            startInfo.ArgumentList.Add(
                resumeRunPath);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Unable to start the WSL production worker.");
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
