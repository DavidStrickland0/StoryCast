using System.Text.Json;
using StoryCast.Application.Preparation;
using StoryCast.Application.Production;
using StoryCast.Domain.Manuscripts;
using StoryCast.Infrastructure.Books;
using StoryCast.Infrastructure.Production;

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
                    "STORYCAST_OLLAMA_MODEL");
            string StageModel(string option, string setting) =>
                GetOptionValue(args, option) ?? AudioGenerationSettings.String(bookPath, setting) ?? model ??
                throw new ArgumentException($"Specify {option}, --model, or STORYCAST_OLLAMA_MODEL.");
            var discoveryModel = StageModel("--discovery-model", "discoveryModel");
            var attributionModel = StageModel("--attribution-model", "attributionModel");
            var castingModel = StageModel("--casting-model", "castingModel");

            var libraryPath = Path.GetFullPath(
                GetOptionValue(args, "--library") ??
                AudioGenerationSettings.String(bookPath, "voiceLibraryPath") ??
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
            var skippedChapters = (GetOptionValue(args, "--skip-chapters") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var chapter in book.Manuscript.Chapters)
            {
                if (skippedChapters.Contains(chapter.Id))
                {
                    continue;
                }
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
                        HasCompletedArtifact(chapterState, prepared.ChapterId));

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
            Console.WriteLine($"Discovery:     {discoveryModel}");
            Console.WriteLine($"Attribution:   {attributionModel}");
            Console.WriteLine($"Casting:       {castingModel}");
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
                CanResumeWorkerRun(entry.RunDirectory);

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
            entry.FailedStage = string.Empty;
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
                        discoveryModel,
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
                    characterExitCode,
                    "character discovery");
            }

            Console.WriteLine();
            Console.WriteLine(
                "===== Dialogue attribution =====");

            string? dialogueFailure = null;
            var dialogueExitCode =
                await DialogueCommands.AttributeAsync(
                    [
                        bookPath,
                        "--model",
                        attributionModel,
                        "--ollama-url",
                        ollamaUrl,
                        "--chapter",
                        target.ChapterId,
                        .. (args.Contains("--allow-review", StringComparer.OrdinalIgnoreCase) ? new[] { "--allow-review" } : Array.Empty<string>())
                    ], message => dialogueFailure = message);

            if (dialogueExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    $"Dialogue attribution failed: {dialogueFailure ?? "No detailed error was returned."}",
                    dialogueExitCode,
                    "dialogue attribution");
            }

            if (args.Contains("--review-dialogue", StringComparer.OrdinalIgnoreCase) || args.Contains("--allow-review", StringComparer.OrdinalIgnoreCase))
            {
                var artifact = await new FileSystemProductionScriptStore().LoadAsync(book, target.ChapterId)
                    ?? throw new InvalidDataException("Attributed chapter script is missing.");
                var uncertain = DialogueReviewPolicy.GetUncertainSegments(artifact.Script.Segments);
                var reviewFolder = Path.Combine(bookPath, "production", "dialogue-review");
                Directory.CreateDirectory(reviewFolder);
                await File.WriteAllTextAsync(Path.Combine(reviewFolder, $"{target.ChapterId}.json"),
                    JsonSerializer.Serialize(new { chapterId = target.ChapterId, sourceSha256 = artifact.SourceSha256,
                        reviewRequired = uncertain.Count > 0, segments = uncertain }));
                if (uncertain.Count > 0 && !args.Contains("--allow-review", StringComparer.OrdinalIgnoreCase))
                {
                    return await RecordFailureAsync(statePath, state, entry,
                        $"Dialogue needs review before synthesis: segments {string.Join(", ", uncertain)}.", 2,
                        "dialogue review");
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                "===== Casting continuity =====");

            var castingExitCode =
                await CastingCommands.AssignAsync(
                    [
                        bookPath,
                        "--model",
                        castingModel,
                        "--ollama-url",
                        ollamaUrl,
                        "--library",
                        libraryPath,
                        "--chapter",
                        target.ChapterId,
                        "--worker-image", workerImage,
                        "--whisper-model", whisperModel,
                        .. (args.Contains("--auto-voices", StringComparer.OrdinalIgnoreCase) ? new[] { "--auto-voices" } : Array.Empty<string>()),
                        .. (args.Contains("--no-auto-voices", StringComparer.OrdinalIgnoreCase) ? new[] { "--no-auto-voices" } : Array.Empty<string>()),
                        .. (GetOptionValue(args, "--voiceforge") is { } forgePath ? new[] { "--voiceforge", forgePath } : Array.Empty<string>()),
                        .. (GetOptionValue(args, "--voiceforge-image") is { } forgeImage ? new[] { "--voiceforge-image", forgeImage } : Array.Empty<string>())
                    ]);

            if (castingExitCode != 0)
            {
                return await RecordFailureAsync(
                    statePath,
                    state,
                    entry,
                    "Casting failed.",
                    castingExitCode,
                    "casting");
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

            if (args.Contains("--allow-review", StringComparer.OrdinalIgnoreCase)) productionArguments.Add("--allow-review");
            if (args.Contains("--no-ambience", StringComparer.OrdinalIgnoreCase)) productionArguments.Add("--no-ambience");
            if (args.Contains("--no-auto-voices", StringComparer.OrdinalIgnoreCase)) productionArguments.Add("--no-auto-voices");
            foreach (var option in new[] { "--soundforge-url", "--ambience-gain-db", "--voiceforge", "--voiceforge-image", "--audio-preparation-model", "--rewrite-model", "--speech-repair-model" })
            {
                if (GetOptionValue(args, option) is { } optionValue) productionArguments.AddRange([option, optionValue]);
            }

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
                    productionExitCode,
                    "chapter production");
            }

            entry.Status = "completed";
            entry.Error = string.Empty;
            entry.FailedStage = string.Empty;
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            entry.UpdatedUtc = entry.CompletedUtc.Value;

            await SaveStateAsync(
                statePath,
                state);

            var audioPath = SoundForgeRuntime.PreferredAudio(entry.RunDirectory, target.ChapterId,
                SoundForgeRuntime.ReadSettings(bookPath, args).Enabled);

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
    /// Processes available chapters until the book is caught up.
    /// </summary>
    /// <param name="args">The continuous-production arguments.</param>
    /// <returns>
    /// A task containing zero when all available chapters complete;
    /// otherwise, a nonzero exit code.
    /// </returns>
    public static async Task<int> ProduceRunAsync(
        string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Continuous production requires a book directory.");

            return 1;
        }

        while (true)
        {
            var exitCode = await ProduceNextAsync(args);

            if (exitCode != 0)
            {
                return exitCode;
            }

            if (!await HasIncompleteChapterAsync(
                    Path.GetFullPath(args[0]), GetOptionValue(args, "--skip-chapters")))
            {
                Console.WriteLine();
                Console.WriteLine(
                    "Production is caught up. " +
                    "No incomplete chapters remain.");

                return 0;
            }

            Console.WriteLine();
            Console.WriteLine(
                "===== Advancing to next chapter =====");
            Console.WriteLine();
        }
    }
    /// <summary>
    /// Displays detailed help for continuous chapter production.
    /// </summary>
    public static void WriteRunHelp()
    {
        Console.WriteLine(
            """
            StoryCast continuous chapter production

            Usage:
              storycast produce run <book-directory> --model <model> [options]

            Behavior:
              Processes chapters in manuscript order. Each chapter completes
              character discovery, dialogue attribution, continuity-preserving
              casting, Docker synthesis, verification, assembly, and mastering
              before the next chapter begins.

              Processing stops when all currently available chapters are
              complete or when one chapter fails. Run the same command again
              to resume the failed chapter and then continue forward.

            Continuity:
              Character identities and casting assignments are book-wide.
              Existing characters and the narrator retain their assigned
              voices across every chapter. New characters receive unused
              verified voices.

            Options:
              --model <model>
                  Shared Ollama text-model fallback for analysis and casting.

              --discovery-model <model>
                  Ollama model for character discovery; overrides --model.
              --attribution-model <model>
                  Ollama model for dialogue speaker attribution; overrides --model.
              --casting-model <model>
                  Ollama model for voice casting; overrides --model.
              --audio-preparation-model <model>
                  Ollama model for speech-text preparation; defaults to rewrite model.
              --speech-repair-model <model>
                  Ollama model for wording repairs after speech verification fails.
                  May rephrase spoken text. Default qwen3.8:27b.
                  --rewrite-model remains a legacy alias.

              --ollama-url <url>
                  Ollama endpoint. Defaults to http://localhost:11434/.

              --library <path>
                  Analyzed verified voice library. Defaults to .\voices.

              --worker-image <image>
                  Docker worker image. Defaults to storycast-worker:dev.

              --whisper-model <model>
                  Whisper verification model. Defaults to small.en.

              --skip-chapters <chapter-ids>
                  Comma-separated chapter IDs to leave for review. Used by
                  ScribbleCast's persistent queue; skipped audio is not approved.

              --auto-voices
                  Enabled by default: reuse attributed voices or generate and
                  verify missing matches with VoiceForge. Existing fallback
                  assignments are rechecked before chapter production.

              --no-auto-voices
                  Disable provisioning; fail if attributed matches are missing.

              --voiceforge <path>
                  VoiceForge project directory; defaults to ../VoiceForge.

              --voiceforge-image <image>
                  VoiceDesign image; defaults to voiceforge-design:dev.

              --review-dialogue
                  Stop before synthesis when dialogue confidence is below 0.8
                  or its speaker is unresolved, unless --allow-review is enabled.

              --allow-review
                  Produce final MP3s with review findings preserved. ScribbleCast
                  enables this option; failed audio integrity still stops export.

              --help, -h
                  Display this help.
            """);
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
              Attributed speech counts are recorded without releasing voices.

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

              --skip-chapters <chapter-ids>
                  Comma-separated chapter IDs to leave for review.

              --review-dialogue
                  Require resolved dialogue speakers and confidence >= 0.8.

              --help, -h
                  Display this help.
            """);
    }

    private static async Task<bool> HasIncompleteChapterAsync(
        string bookPath, string? skipped = null)
    {
        var book = await new FileSystemBookProjectLoader()
            .LoadAsync(bookPath);

        var statePath = Path.Combine(
            book.RootPath,
            "production",
            "incremental-production.json");

        var state = await LoadStateAsync(
            statePath,
            book.Id);

        var preparer = new ChapterTextPreparer();
        var skippedChapters = (skipped ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return book.Manuscript.Chapters.Any(
            chapter =>
            {
                if (skippedChapters.Contains(chapter.Id))
                {
                    return false;
                }
                var prepared = preparer.Prepare(chapter);

                return !state.Chapters.Any(
                    chapterState =>
                        string.Equals(
                            chapterState.ChapterId,
                            prepared.ChapterId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            chapterState.SourceSha256,
                            prepared.SourceSha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        HasCompletedArtifact(chapterState, prepared.ChapterId));
            });
    }

    private static bool CanResumeWorkerRun(string runDirectory)
    {
        var manifestPath = Path.Combine(runDirectory, "run.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        // A completed worker run is immutable unless explicitly remixed. If
        // bookkeeping or review selects the chapter again, use a new directory.
        return !manifest.RootElement.TryGetProperty("status", out var status) ||
            !string.Equals(status.GetString(), "completed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a completed state entry still has its mastered
    /// chapter audio.
    /// </summary>
    /// <param name="chapterState">
    /// The persisted incremental chapter state.
    /// </param>
    /// <param name="chapterId">
    /// The expected chapter identifier.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the entry is complete and its mastered
    /// audio still exists.
    /// </returns>
    private static bool HasCompletedArtifact(
        IncrementalChapterState chapterState,
        string chapterId)
    {
        if (!string.Equals(
                chapterState.Status,
                "completed",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(
                chapterState.RunDirectory))
        {
            return false;
        }

        var masteredAudioPath = Path.Combine(
            chapterState.RunDirectory,
            chapterId,
            $"{chapterId}.mastered.wav");

        return File.Exists(masteredAudioPath);
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
        int exitCode,
        string failedStage = "")
    {
        entry.Status = "failed";
        entry.Error = message;
        entry.FailedStage = failedStage;
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

        public string FailedStage { get; set; } = string.Empty;

        public DateTimeOffset UpdatedUtc { get; set; }

        public DateTimeOffset? CompletedUtc { get; set; }
    }
}
