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

            var workerImage =
                GetOptionValue(
                    args,
                    "--worker-image") ??
                "storycast-worker:dev";

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

            var runtime = new DockerWorkerRuntime(
                bookPath,
                libraryPath,
                workerImage);

            var containerBookPath =
                runtime.GetBookPath();

            var containerRunDirectoryPath =
                runDirectoryPath is null
                    ? null
                    : runtime.GetBookPath(
                        runDirectoryPath);

            var containerResumeRunPath =
                resumeRunPath is null
                    ? null
                    : runtime.GetBookPath(
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
                runtime,
                Path.GetFileName(scriptPath),
                containerBookPath,
                runtime.LibraryPath,
                chapterId,
                whisperModel,
                containerRunDirectoryPath,
                containerResumeRunPath);

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

    private static Task<int> RunWorkerAsync(
        DockerWorkerRuntime runtime,
        string scriptName,
        string bookPath,
        string libraryPath,
        string chapterId,
        string whisperModel,
        string? runDirectoryPath,
        string? resumeRunPath)
    {
        var arguments = new List<string>
        {
            bookPath,
            libraryPath,
            "--chapter",
            chapterId,
            "--whisper-model",
            whisperModel
        };

        if (runDirectoryPath is not null)
        {
            arguments.Add("--run-directory");
            arguments.Add(runDirectoryPath);
        }

        if (resumeRunPath is not null)
        {
            arguments.Add("--resume-run-directory");
            arguments.Add(resumeRunPath);
        }

        return runtime.RunAsync(
            scriptName,
            arguments);
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
