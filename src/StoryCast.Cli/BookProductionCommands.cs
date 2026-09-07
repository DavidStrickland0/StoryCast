using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StoryCast.Infrastructure.Books;

internal static class BookProductionCommands
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

    /// <summary>
    /// Produces every configured chapter in manuscript order.
    /// </summary>
    /// <param name="args">The book-production arguments.</param>
    /// <returns>
    /// A task containing zero on success; otherwise, a nonzero exit code.
    /// </returns>
    public static async Task<int> ProduceBookAsync(
        string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Book production requires a book directory.");

            return 1;
        }

        BookRunManifest? manifest = null;
        string? manifestPath = null;

        try
        {
            var bookPath = Path.GetFullPath(args[0]);

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

            var assemblyScriptPath = Path.GetFullPath(
                GetOptionValue(
                    args,
                    "--book-assembly-script") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "tools",
                    "assemble_book.py"));

            var chapterPauseText =
                GetOptionValue(
                    args,
                    "--chapter-pause") ??
                "1.0";

            if (!double.TryParse(
                    chapterPauseText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var chapterPauseSeconds) ||
                !double.IsFinite(chapterPauseSeconds) ||
                chapterPauseSeconds < 0)
            {
                throw new ArgumentException(
                    "--chapter-pause must be a finite " +
                    "nonnegative number.");
            }

            var whisperModel =
                GetOptionValue(
                    args,
                    "--whisper-model") ??
                "small.en";

            var resumeRunValue =
                GetOptionValue(
                    args,
                    "--resume-book-run");

            var resumeRunPath =
                resumeRunValue is null
                    ? null
                    : Path.GetFullPath(
                        resumeRunValue);

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

            if (!File.Exists(assemblyScriptPath))
            {
                throw new FileNotFoundException(
                    $"Book assembly worker was not found: " +
                    $"{assemblyScriptPath}",
                    assemblyScriptPath);
            }

            var loader = new FileSystemBookProjectLoader();
            var book = await loader.LoadAsync(bookPath);

            string runDirectory;

            if (resumeRunPath is not null)
            {
                runDirectory = resumeRunPath;

                if (!Directory.Exists(runDirectory))
                {
                    throw new DirectoryNotFoundException(
                        $"Resume book-run directory was not found: " +
                        $"{runDirectory}");
                }

                manifestPath = Path.Combine(
                    runDirectory,
                    "book-run.json");

                manifest = await LoadBookRunAsync(
                    manifestPath);

                if (manifest.SchemaVersion != 1)
                {
                    throw new InvalidDataException(
                        "Resume book-run schema version is unsupported.");
                }

                if (manifest.Status == "completed")
                {
                    throw new InvalidOperationException(
                        "A completed book run cannot be resumed.");
                }

                if (!string.Equals(
                        manifest.BookId,
                        book.Id,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        Path.GetFullPath(manifest.BookPath),
                        book.RootPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetFullPath(
                            manifest.VoiceLibraryPath),
                        libraryPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        manifest.Settings.WhisperModel,
                        whisperModel,
                        StringComparison.Ordinal) ||
                    manifest.Settings.ChapterPauseSeconds !=
                        chapterPauseSeconds)
                {
                    throw new InvalidOperationException(
                        "Resume book-run identity or settings do not " +
                        "match the requested production.");
                }

                var expectedChapterIds =
                    book.Manuscript.Chapters
                        .OrderBy(chapter => chapter.Index)
                        .Select(chapter => chapter.Id)
                        .ToArray();

                var actualChapterIds =
                    manifest.Chapters
                        .OrderBy(chapter => chapter.Index)
                        .Select(chapter => chapter.ChapterId)
                        .ToArray();

                if (!expectedChapterIds.SequenceEqual(
                        actualChapterIds,
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Resume book-run chapters do not match the " +
                        "current book manifest.");
                }

                manifest.Status = "running";
                manifest.CurrentChapterId = null;
                manifest.FailedChapterId = null;
                manifest.CompletedUtc = null;
                manifest.Error = null;
                manifest.ResumeCount++;
                manifest.LastResumedUtc =
                    DateTimeOffset.UtcNow;
            }
            else
            {
                var timestamp = DateTimeOffset.UtcNow.ToString(
                    "yyyyMMdd-HHmmss-fff");

                var runId =
                    $"{timestamp}-book-{Guid.NewGuid():N}"[..(
                        timestamp.Length + 14)];

                runDirectory = Path.Combine(
                    book.RootPath,
                    "output",
                    runId);

                var chaptersDirectory = Path.Combine(
                    runDirectory,
                    "chapters");

                Directory.CreateDirectory(
                    chaptersDirectory);

                manifestPath = Path.Combine(
                    runDirectory,
                    "book-run.json");

                manifest = new BookRunManifest
                {
                    SchemaVersion = 1,
                    RunId = runId,
                    BookId = book.Id,
                    Title = book.Title,
                    Author = book.Author,
                    BookPath = book.RootPath,
                    VoiceLibraryPath = libraryPath,
                    StartedUtc = DateTimeOffset.UtcNow,
                    Status = "running",
                    Settings = new BookRunSettings
                    {
                        WhisperModel = whisperModel,
                        ChapterPauseSeconds =
                            chapterPauseSeconds
                    },
                    Chapters = book.Manuscript.Chapters
                        .Select(
                            chapter => new BookRunChapter
                            {
                                Index = chapter.Index,
                                ChapterId = chapter.Id,
                                Status = "pending",
                                RunDirectory = Path.Combine(
                                    chaptersDirectory,
                                    chapter.Id)
                            })
                        .ToList()
                };
            }

            await WriteManifestAsync(
                manifestPath,
                manifest);
            Console.WriteLine($"Book:          {book.Title}");
            Console.WriteLine($"Book ID:       {book.Id}");
            Console.WriteLine(
                $"Chapters:      {manifest.Chapters.Count}");
            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Whisper:       {whisperModel}");
            Console.WriteLine(
                $"Chapter pause: {chapterPauseSeconds:F2} seconds");
            Console.WriteLine($"Run:           {runDirectory}");

            if (resumeRunPath is not null)
            {
                Console.WriteLine(
                    $"Resume count:  {manifest.ResumeCount}");
            }

            foreach (var chapter in manifest.Chapters)
            {
                if (chapter.Status == "completed")
                {
                    if (string.IsNullOrWhiteSpace(
                            chapter.MasteredAudioPath) ||
                        !File.Exists(
                            chapter.MasteredAudioPath))
                    {
                        throw new InvalidDataException(
                            $"Completed chapter " +
                            $"{chapter.ChapterId} is missing its " +
                            "mastered audio.");
                    }

                    Console.WriteLine();
                    Console.WriteLine(
                        $"===== Book chapter " +
                        $"{chapter.Index + 1}/" +
                        $"{manifest.Chapters.Count}: " +
                        $"{chapter.ChapterId} (reused) =====");

                    continue;
                }

                manifest.CurrentChapterId =
                    chapter.ChapterId;
                chapter.Status = "running";
                chapter.ExitCode = null;

                await WriteManifestAsync(
                    manifestPath,
                    manifest);

                Console.WriteLine();
                Console.WriteLine(
                    $"===== Book chapter " +
                    $"{chapter.Index + 1}/" +
                    $"{manifest.Chapters.Count}: " +
                    $"{chapter.ChapterId} =====");

                var chapterRunManifestPath = Path.Combine(
                    chapter.RunDirectory,
                    "run.json");

                var hasChapterRun =
                    File.Exists(chapterRunManifestPath);

                if (!hasChapterRun &&
                    Directory.Exists(
                        chapter.RunDirectory))
                {
                    throw new InvalidDataException(
                        $"Chapter run directory exists without " +
                        $"run.json: {chapter.RunDirectory}");
                }

                var chapterArguments = new List<string>
                {
                    book.RootPath,
                    chapter.ChapterId,
                    "--library",
                    libraryPath,
                    "--script",
                    scriptPath,
                    "--whisper-model",
                    whisperModel,
                    "--python",
                    python,
                    "--cuda-library-path",
                    cudaLibraryPath
                };

                if (hasChapterRun)
                {
                    chapterArguments.Add(
                        "--resume-run");
                }
                else
                {
                    chapterArguments.Add(
                        "--run-directory");
                }

                chapterArguments.Add(
                    chapter.RunDirectory);

                var exitCode =
                    await ProductionCommands.ProduceChapterAsync(
                        [.. chapterArguments]);

                chapter.ExitCode = exitCode;

                if (exitCode != 0)
                {
                    chapter.Status = "failed";
                    manifest.Status = "failed";
                    manifest.FailedChapterId =
                        chapter.ChapterId;
                    manifest.CompletedUtc =
                        DateTimeOffset.UtcNow;

                    await WriteManifestAsync(
                        manifestPath,
                        manifest);

                    Console.Error.WriteLine(
                        $"Book production stopped at " +
                        $"{chapter.ChapterId}.");

                    return exitCode;
                }

                var chapterRun = await LoadChapterRunAsync(
                    chapterRunManifestPath);

                if (!string.Equals(
                        chapterRun.Status,
                        "completed",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Chapter run did not report completion: " +
                        $"{chapterRunManifestPath}");
                }

                var masteredAudioPath = Path.Combine(
                    chapter.RunDirectory,
                    chapter.ChapterId,
                    $"{chapter.ChapterId}.mastered.wav");

                if (!File.Exists(masteredAudioPath))
                {
                    throw new FileNotFoundException(
                        $"Mastered chapter audio was not found: " +
                        $"{masteredAudioPath}",
                        masteredAudioPath);
                }

                chapter.Status = "completed";
                chapter.MasteredAudioPath =
                    masteredAudioPath;

                await WriteManifestAsync(
                    manifestPath,
                    manifest);
            }
            manifest.Status = "assembling";
            manifest.CurrentChapterId = null;
            manifest.CompletedUtc = null;
            manifest.Error = null;

            await WriteManifestAsync(
                manifestPath,
                manifest);

            var assemblyRequestPath = Path.Combine(
                runDirectory,
                "book-assembly-request.json");

            var wslRunDirectory =
                await ConvertToWslPathAsync(
                    runDirectory);

            var wslAssemblyScript =
                await ConvertToWslPathAsync(
                    assemblyScriptPath);

            var assemblyChapters =
                new List<BookAssemblyChapterRequest>();

            foreach (var chapter in manifest.Chapters)
            {
                if (string.IsNullOrWhiteSpace(
                        chapter.MasteredAudioPath))
                {
                    throw new InvalidDataException(
                        $"Chapter {chapter.ChapterId} has no " +
                        "mastered audio path.");
                }

                assemblyChapters.Add(
                    new BookAssemblyChapterRequest
                    {
                        Index = chapter.Index,
                        ChapterId = chapter.ChapterId,
                        AudioPath =
                            await ConvertToWslPathAsync(
                                chapter.MasteredAudioPath)
                    });
            }

            var assemblyRequest =
                new BookAssemblyRequest
                {
                    SchemaVersion = 1,
                    BookId = manifest.BookId,
                    Title = manifest.Title,
                    Author = manifest.Author,
                    RunDirectory = wslRunDirectory,
                    ChapterPauseSeconds =
                        manifest.Settings.ChapterPauseSeconds,
                    Chapters = assemblyChapters
                };

            await WriteAssemblyRequestAsync(
                assemblyRequestPath,
                assemblyRequest);

            var wslAssemblyRequest =
                await ConvertToWslPathAsync(
                    assemblyRequestPath);

            var assemblyExitCode =
                await RunAssemblyWorkerAsync(
                    python,
                    wslAssemblyScript,
                    wslAssemblyRequest);

            if (assemblyExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Book assembly failed with exit code " +
                    $"{assemblyExitCode}.");
            }

            var audiobookPath = Path.Combine(
                runDirectory,
                $"{manifest.BookId}.mastered.wav");

            var assemblyManifestPath = Path.Combine(
                runDirectory,
                "book-assembly.json");

            if (!File.Exists(audiobookPath))
            {
                throw new FileNotFoundException(
                    $"Assembled audiobook was not found: " +
                    $"{audiobookPath}",
                    audiobookPath);
            }

            if (!File.Exists(assemblyManifestPath))
            {
                throw new FileNotFoundException(
                    $"Book assembly manifest was not found: " +
                    $"{assemblyManifestPath}",
                    assemblyManifestPath);
            }

            manifest.Status = "completed";
            manifest.CurrentChapterId = null;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.AudioPath = audiobookPath;
            manifest.AssemblyManifestPath =
                assemblyManifestPath;

            await WriteManifestAsync(
                manifestPath,
                manifest);

            Console.WriteLine();
            Console.WriteLine("Audiobook production complete.");
            Console.WriteLine($"Run:      {runDirectory}");
            Console.WriteLine($"Audio:    {audiobookPath}");
            Console.WriteLine(
                $"Manifest: {manifestPath}");

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            JsonException)
        {
            if (manifest is not null &&
                manifestPath is not null)
            {
                manifest.Status = "failed";
                manifest.CompletedUtc =
                    DateTimeOffset.UtcNow;
                manifest.Error = exception.Message;

                try
                {
                    await WriteManifestAsync(
                        manifestPath,
                        manifest);
                }
                catch
                {
                    // Preserve the original production failure.
                }
            }

            Console.Error.WriteLine(
                $"Book production failed: {exception.Message}");

            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task WriteAssemblyRequestAsync(
        string path,
        BookAssemblyRequest request)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path) ??
                throw new InvalidDataException(
                    $"Request has no parent directory: {path}"),
            $".{Path.GetFileName(path)}.tmp");

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                request,
                SerializerOptions);

            await stream.WriteAsync("\n"u8.ToArray());
        }

        File.Move(
            temporaryPath,
            path,
            overwrite: true);
    }

    private static async Task<int> RunAssemblyWorkerAsync(
        string python,
        string scriptPath,
        string requestPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add(python);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(requestPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Unable to start the WSL book assembly worker.");
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
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

    private static async Task<BookRunManifest>
        LoadBookRunAsync(
            string path)
    {
        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<
            BookRunManifest>(
                stream,
                SerializerOptions) ??
            throw new InvalidDataException(
                $"Book run manifest could not be read: {path}");
    }

    private static async Task<ChapterRunSummary>
        LoadChapterRunAsync(
            string path)
    {
        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<
            ChapterRunSummary>(
                stream,
                SerializerOptions) ??
            throw new InvalidDataException(
                $"Chapter run manifest could not be read: {path}");
    }

    private static async Task WriteManifestAsync(
        string path,
        BookRunManifest manifest)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path) ??
                throw new InvalidDataException(
                    $"Manifest has no parent directory: {path}"),
            $".{Path.GetFileName(path)}.tmp");

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                manifest,
                SerializerOptions);

            await stream.WriteAsync("\n"u8.ToArray());
        }

        File.Move(
            temporaryPath,
            path,
            overwrite: true);
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

    private sealed class BookRunManifest
    {
        public required int SchemaVersion { get; init; }

        public required string RunId { get; init; }

        public required string BookId { get; init; }

        public required string Title { get; init; }

        public required string Author { get; init; }

        public required string BookPath { get; init; }

        public required string VoiceLibraryPath { get; init; }

        public required DateTimeOffset StartedUtc { get; init; }

        public DateTimeOffset? CompletedUtc { get; set; }

        public required string Status { get; set; }

        public string? CurrentChapterId { get; set; }

        public string? FailedChapterId { get; set; }

        public string? Error { get; set; }

        public required BookRunSettings Settings { get; init; }

        public required List<BookRunChapter> Chapters { get; init; }

        public int ResumeCount { get; set; }

        public DateTimeOffset? LastResumedUtc { get; set; }

        public string? AudioPath { get; set; }

        public string? AssemblyManifestPath { get; set; }
    }

    private sealed class BookAssemblyRequest
    {
        public required int SchemaVersion { get; init; }

        public required string BookId { get; init; }

        public required string Title { get; init; }

        public required string Author { get; init; }

        public required string RunDirectory { get; init; }

        public required double ChapterPauseSeconds { get; init; }

        public required List<BookAssemblyChapterRequest>
            Chapters { get; init; }
    }

    private sealed class BookAssemblyChapterRequest
    {
        public required int Index { get; init; }

        public required string ChapterId { get; init; }

        public required string AudioPath { get; init; }
    }

    private sealed class BookRunSettings
    {
        public required string WhisperModel { get; init; }

        public required double ChapterPauseSeconds { get; init; }
    }

    private sealed class BookRunChapter
    {
        public required int Index { get; init; }

        public required string ChapterId { get; init; }

        public required string Status { get; set; }

        public required string RunDirectory { get; init; }

        public int? ExitCode { get; set; }

        public string? MasteredAudioPath { get; set; }
    }

    private sealed class ChapterRunSummary
    {
        public string? Status { get; init; }

        public string? MasteredAudioPath { get; init; }
    }
}