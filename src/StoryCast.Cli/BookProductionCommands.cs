using System.Text.Json;
using StoryCast.Infrastructure.Books;

internal static class BookProductionCommands
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
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

            var whisperModel =
                GetOptionValue(
                    args,
                    "--whisper-model") ??
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

            var loader = new FileSystemBookProjectLoader();
            var book = await loader.LoadAsync(bookPath);

            var timestamp = DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmss-fff");

            var runId =
                $"{timestamp}-book-{Guid.NewGuid():N}"[..(
                    timestamp.Length + 14)];

            var runDirectory = Path.Combine(
                book.RootPath,
                "output",
                runId);

            var chaptersDirectory = Path.Combine(
                runDirectory,
                "chapters");

            Directory.CreateDirectory(chaptersDirectory);

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
                    WhisperModel = whisperModel
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

            await WriteManifestAsync(
                manifestPath,
                manifest);

            Console.WriteLine($"Book:          {book.Title}");
            Console.WriteLine($"Book ID:       {book.Id}");
            Console.WriteLine(
                $"Chapters:      {manifest.Chapters.Count}");
            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Whisper:       {whisperModel}");
            Console.WriteLine($"Run:           {runDirectory}");

            foreach (var chapter in manifest.Chapters)
            {
                manifest.CurrentChapterId = chapter.ChapterId;
                chapter.Status = "running";

                await WriteManifestAsync(
                    manifestPath,
                    manifest);

                Console.WriteLine();
                Console.WriteLine(
                    $"===== Book chapter " +
                    $"{chapter.Index + 1}/" +
                    $"{manifest.Chapters.Count}: " +
                    $"{chapter.ChapterId} =====");

                var chapterArguments = new[]
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
                    cudaLibraryPath,
                    "--run-directory",
                    chapter.RunDirectory
                };

                var exitCode =
                    await ProductionCommands.ProduceChapterAsync(
                        chapterArguments);

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

                var chapterRunManifestPath = Path.Combine(
                    chapter.RunDirectory,
                    "run.json");

                var chapterRun = await LoadChapterRunAsync(
                    chapterRunManifestPath);

                chapter.Status = "completed";
                chapter.MasteredAudioPath =
                    chapterRun.MasteredAudioPath;

                await WriteManifestAsync(
                    manifestPath,
                    manifest);
            }

            manifest.Status = "completed";
            manifest.CurrentChapterId = null;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;

            await WriteManifestAsync(
                manifestPath,
                manifest);

            Console.WriteLine();
            Console.WriteLine("Book chapter production complete.");
            Console.WriteLine($"Run:      {runDirectory}");
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
    }

    private sealed class BookRunSettings
    {
        public required string WhisperModel { get; init; }
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
        public string? MasteredAudioPath { get; init; }
    }
}