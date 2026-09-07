using System.Text.Json;
using StoryCast.Application.Preparation;
using StoryCast.Domain.Manuscripts;
using StoryCast.Infrastructure.Books;

internal static class IncrementalProductionCommands
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    /// <summary>
    /// Processes the next incomplete chapter through analysis, casting, and
    /// Docker-backed audiobook production.
    /// </summary>
    /// <param name="args">The incremental-production arguments.</param>
    /// <returns>
    /// A task containing zero on success or when caught up; otherwise, a
    /// nonzero exit code.
    /// </returns>
    public static async Task<int> ProduceNextAsync(
        string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Incremental production requires a book directory.");

            return 1;
        }

        var statePath = string.Empty;
        IncrementalProductionState? state = null;
        IncrementalChapterState? entry = null;

        try
        {
            var bookPath = Path.GetFullPath(args[0]);

            var model =
                GetOptionValue(args, "--model") ??
                Environment.GetEnvironmentVariable(
                    "STORYCAST_OLLAMA_MODEL") ??
                throw new ArgumentException(
                    "Specify --model or set STORYCAST_OLLAMA_MODEL.");

            var libraryPath = Path.GetFullPath(
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices"));

            var workerImage =
                GetOptionValue(args, "--worker-image") ??
                "storycast-worker:dev";

            var whisperModel =
                GetOptionValue(args, "--whisper-model") ??
                "small.en";

            var ollamaUrl =
                GetOptionValue(args, "--ollama-url") ??
                "http://localhost:11434/";

            if (!ollamaUrl.EndsWith(
                    "/",
                    StringComparison.Ordinal))
            {
                ollamaUrl += "/";
            }

            var book = await new FileSystemBookProjectLoader()
                .LoadAsync(bookPath);

            statePath = Path.Combine(
                book.RootPath,
                "production",
                "incremental-production.json");

            state = await LoadStateAsync(
                statePath,
                book.Id);

            var preparer = new ChapterTextPreparer();
            PreparedChapter? target = null;

            foreach (var chapter in book.Manuscript.Chapters)
            {
                var prepared = preparer.Prepare(chapter);

                var completed = state.Chapters.Any(
                    chapterState =>
                        string.Equals(
                            chapterState.ChapterId,
                            prepared.ChapterId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            chapterState.SourceSha256,
                            prepared.SourceSha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            chapterState.Status,
                            "completed",
                            StringComparison.OrdinalIgnoreCase));

                if (!completed)
                {
                    target = prepared;
                    break;
                }
            }

            Console.WriteLine($"Book:          {book.Title}");
            Console.WriteLine($"Book ID:       {book.Id}");
            Console.WriteLine(
                $"Chapters:      {book.Manuscript.Chapters.Count}");
            Console.WriteLine($"Voice library: {libraryPath}");
            Console.WriteLine($"Model:         {model}");
            Console.WriteLine($"Whisper:       {whisperModel}");
            Console.WriteLine($"Worker image:  {workerImage}");
            Console.WriteLine($"State:         {statePath}");
            Console.WriteLine();

            if (target is null)
            {
                Console.WriteLine(
                    "Incremental production is caught up. " +
                    "No incomplete chapters were found.");

                return 0;
            }

            entry = state.Chapters.FirstOrDefault(
                chapterState =>
                    string.Equals(
                        chapterState.ChapterId,
                        target.ChapterId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        chapterState.SourceSha256,
                        target.SourceSha256,
                        StringComparison.OrdinalIgnoreCase));

            var resumeRun =
                entry is not null &&
                !string.IsNullOrWhiteSpace(entry.RunDirectory) &&
                Directory.Exists(entry.RunDirectory);

            if (entry is null)
            {
                entry = new IncrementalChapterState
                {
                    ChapterId = target.ChapterId,
                    SourceSha256 = target.SourceSha256,
                    Status = "pending",
                    RunDirectory = CreateRunDirectoryPath(
                        book.RootPath,
                        target.ChapterId)
                };

                state.Chapters.RemoveAll(
                    chapterState =>
                        string.Equals(
                            chapterState.ChapterId,
                            target.ChapterId,
                            StringComparison.OrdinalIgnoreCase));

                state.Chapters.Add(entry);
            }
            else if (!resumeRun)
            {
                entry.RunDirectory = CreateRunDirectoryPath(
                    book.RootPath,
                    target.ChapterId);
            }

            entry.Status = "analyzing";
            entry.Error = string.Empty;
            entry.UpdatedUtc = DateTimeOffset.UtcNow;

            await SaveStateAsync(
                statePath,
                state);

            Console.WriteLine(
                $"Next chapter:  {target.ChapterId}");

            Console.WriteLine(
                $"Run:           {entry.RunDirectory}");

            Console.WriteLine(
                $"Resume:        {(resumeRun ? "yes" : "no")}");

            Console.WriteLine();
            Console.WriteLine(
                "===== Character discovery =====");

            var characterExitCode =
                await CharacterCommands.DiscoverAsync(
                    [
                        bookPath,
                        "--model",
                        model,
                        "--ollama-url",
                        ollamaUrl,
                        "--chapter",
                        target.ChapterId
                    ]);

            if (characterExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    "Character discovery failed.",
                    characterExitCode);
            }

            Console.WriteLine();
            Console.WriteLine(
                "===== Dialogue attribution =====");

            var dialogueExitCode =
                await DialogueCommands.AttributeAsync(
                    [
                        bookPath,
                        "--model",
                        model,
                        "--ollama-url",
                        ollamaUrl,
                        "--chapter",
                        target.ChapterId
                    ]);

            if (dialogueExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    "Dialogue attribution failed.",
                    dialogueExitCode);
            }

            Console.WriteLine();
            Console.WriteLine(
                "===== Casting continuity =====");

            var castingExitCode =
                await CastingCommands.AssignAsync(
                    [
                        bookPath,
                        "--model",
                        model,
                        "--ollama-url",
                        ollamaUrl,
                        "--library",
                        libraryPath
                    ]);

            if (castingExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    "Casting failed.",
                    castingExitCode);
            }

            entry.Status = "producing";
            entry.UpdatedUtc = DateTimeOffset.UtcNow;

            await SaveStateAsync(
                statePath,
                state);

            Console.WriteLine();
            Console.WriteLine(
                "===== Chapter production =====");

            var productionArguments = new List<string>
            {
                bookPath,
                target.ChapterId,
                "--library",
                libraryPath,
                "--worker-image",
                workerImage,
                "--whisper-model",
                whisperModel
            };

            productionArguments.Add(
                resumeRun
                    ? "--resume-run"
                    : "--run-directory");

            productionArguments.Add(entry.RunDirectory);

            var productionExitCode =
                await ProductionCommands.ProduceChapterAsync(
                    productionArguments.ToArray());

            if (productionExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    "Chapter production failed.",
                    productionExitCode);
            }

            entry.Status = "completed";
            entry.Error = string.Empty;
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            entry.UpdatedUtc = entry.CompletedUtc.Value;

            await SaveStateAsync(
                statePath,
                state);

            var audioPath = Path.Combine(
                entry.RunDirectory,
                target.ChapterId,
                $"{target.ChapterId}.mastered.wav");

            Console.WriteLine();
            Console.WriteLine(
                "Incremental chapter production complete.");

            Console.WriteLine(
                $"Chapter: {target.ChapterId}");

            Console.WriteLine(
                $"Audio:   {audioPath}");

            Console.WriteLine(
                $"State:   {statePath}");

            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            HttpRequestException or
            JsonException)
        {
            if (state is not null &&
                entry is not null &&
                !string.IsNullOrWhiteSpace(statePath))
            {
                entry.Status = "failed";
                entry.Error = exception.Message;
                entry.UpdatedUtc = DateTimeOffset.UtcNow;

                try
                {
                    await SaveStateAsync(
                        statePath,
                        state);
                }
                catch
                {
                    // Preserve the original failure as the command result.
                }
            }

            Console.Error.WriteLine(
                $"Incremental production failed: {exception.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Displays detailed help for incremental chapter production.
    /// </summary>
    public static void WriteHelp()
    {
        Console.WriteLine(
            """
            StoryCast incremental chapter production

            Usage:
              storycast produce next <book-directory> --model <model> [options]

            Behavior:
              Selects the first chapter without a completed artifact for its
              current source hash. It performs character discovery, dialogue
              attribution, continuity-preserving casting, and Docker-backed
              chapter production for that chapter only.

              Run the same command again to process the following chapter.
              A failed chapter resumes from its existing production run.
              When every current chapter is complete, the command exits 0
              without creating a new run.

            Continuity:
              Character identities are stored book-wide in
              production\characters.json. Existing character and narrator
              voice assignments in production\casting.json are preserved.
              Only newly discovered roles receive unused verified voices.

            State:
              Incremental progress is stored in
              production\incremental-production.json. Chapter audio is stored
              beneath the book's output directory.

            Options:
              --model <model>
                  Installed Ollama model used for character discovery,
                  dialogue attribution, and casting.

              --ollama-url <url>
                  Ollama endpoint. Defaults to http://localhost:11434/.

              --library <path>
                  Analyzed verified voice library. Defaults to .\voices.

              --worker-image <image>
                  Docker worker image. Defaults to storycast-worker:dev.

              --whisper-model <model>
                  Whisper verification model. Defaults to small.en.

              --help, -h
                  Display this help.
            """);
    }

    private static async Task<IncrementalProductionState>
        LoadStateAsync(
            string path,
            string bookId)
    {
        if (!File.Exists(path))
        {
            return new IncrementalProductionState
            {
                SchemaVersion = 1,
                BookId = bookId,
                Chapters = []
            };
        }

        await using var stream = File.OpenRead(path);

        var state =
            await JsonSerializer.DeserializeAsync<
                IncrementalProductionState>(
                    stream,
                    SerializerOptions) ??
            throw new InvalidDataException(
                $"Incremental state is empty: {path}");

        if (!string.Equals(
                state.BookId,
                bookId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Incremental state belongs to book " +
                $"'{state.BookId}', not '{bookId}'.");
        }

        return state;
    }

    private static async Task SaveStateAsync(
        string path,
        IncrementalProductionState state)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                $"State path has no parent directory: {path}");

        Directory.CreateDirectory(directory);

        var temporaryPath =
            $"{path}.{Guid.NewGuid():N}.tmp";

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
                    state,
                    SerializerOptions);
            }

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<int> RecordFailureAsync(
        string statePath,
        IncrementalProductionState state,
        IncrementalChapterState entry,
        string message,
        int exitCode)
    {
        entry.Status = "failed";
        entry.Error = message;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;

        await SaveStateAsync(
            statePath,
            state);

        Console.Error.WriteLine(message);

        return exitCode == 0
            ? 1
            : exitCode;
    }

    private static string CreateRunDirectoryPath(
        string bookRoot,
        string chapterId)
    {
        var runId =
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-" +
            $"incremental-{chapterId}-" +
            $"{Guid.NewGuid():N}"[..8];

        return Path.Combine(
            bookRoot,
            "output",
            runId);
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

    private sealed class IncrementalProductionState
    {
        public int SchemaVersion { get; init; }

        public string BookId { get; init; } =
            string.Empty;

        public List<IncrementalChapterState> Chapters
        { get; init; } = [];
    }

    private sealed class IncrementalChapterState
    {
        public string ChapterId { get; init; } =
            string.Empty;

        public string SourceSha256 { get; init; } =
            string.Empty;

        public string Status { get; set; } =
            string.Empty;

        public string RunDirectory { get; set; } =
            string.Empty;

        public string Error { get; set; } =
            string.Empty;

        public DateTimeOffset UpdatedUtc { get; set; }

        public DateTimeOffset? CompletedUtc { get; set; }
    }
}