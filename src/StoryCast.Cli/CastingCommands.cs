using StoryCast.Application.Casting;
using StoryCast.Application.Voices;
using StoryCast.Infrastructure.Books;
using StoryCast.Infrastructure.Casting;
using StoryCast.Infrastructure.Characters;
using StoryCast.Infrastructure.Voices;
using StoryCast.Ollama;
using StoryCast.Infrastructure.Production;
using StoryCast.Application.Preparation;

internal static class CastingCommands
{
    public static async Task<int> AssignAsync(string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Automatic casting requires a book directory.");

            return 1;
        }

        var bookPath = args[0];

        try
        {
            var model =
                GetOptionValue(args, "--model") ??
                Environment.GetEnvironmentVariable(
                    "STORYCAST_OLLAMA_MODEL") ??
                throw new ArgumentException(
                    "Specify --model or set STORYCAST_OLLAMA_MODEL.");

            var libraryPath =
                GetOptionValue(args, "--library") ??
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "voices");

            var ollamaUrl =
                GetOptionValue(args, "--ollama-url") ??
                "http://localhost:11434/";

            if (!ollamaUrl.EndsWith(
                    "/",
                    StringComparison.Ordinal))
            {
                ollamaUrl += "/";
            }

            return await ExecuteAsync(
                bookPath,
                libraryPath,
                model,
                ollamaUrl,
                GetOptionValue(args, "--chapter"),
                args.Contains("--auto-voices", StringComparer.OrdinalIgnoreCase),
                GetOptionValue(args, "--voiceforge") ?? Path.GetFullPath(Path.Combine("..", "VoiceForge")),
                GetOptionValue(args, "--voiceforge-image") ?? "voiceforge-design:dev",
                GetOptionValue(args, "--worker-image") ?? "storycast-worker:dev",
                GetOptionValue(args, "--whisper-model") ?? "small.en");
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(
        string bookPath,
        string libraryPath,
        string model,
        string ollamaUrl,
        string? chapterId, bool autoVoices, string voiceForge, string voiceForgeImage, string workerImage, string whisper)
    {
        try
        {
            var bookLoader =
                new FileSystemBookProjectLoader();

            var book = await bookLoader.LoadAsync(
                bookPath);

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(
                    ollamaUrl,
                    UriKind.Absolute),

                Timeout = TimeSpan.FromMinutes(30)
            };

            var generator =
                new OllamaStructuredTextGenerator(
                    httpClient,
                    model);

            var validator =
                new CastingAssignmentValidator();

            IVoiceLibrary voiceLibrary =
                new FileSystemVoiceLibrary();

            IVerifiedVoiceLibrary verifiedVoiceLibrary =
                new FileSystemVerifiedVoiceLibrary(
                    voiceLibrary);

            var workflow = new CastingWorkflow(
                new FileSystemCharacterRegistryStore(),
                verifiedVoiceLibrary,
                new AutomaticCastingService(
                    generator,
                    validator),
                validator,
                new FileSystemCastingPlanStore());

            Console.WriteLine($"Book:          {book.Title}");
            Console.WriteLine($"Book ID:       {book.Id}");
            Console.WriteLine(
                $"Voice library: {Path.GetFullPath(libraryPath)}");
            Console.WriteLine($"Model:         {model}");
            Console.WriteLine($"Ollama:        {ollamaUrl}");
            Console.WriteLine();
            Console.WriteLine("Assigning voices...");

            VoiceUsageContext? usage = null;
            IReadOnlySet<string>? requiredSpeakers = null;
            if (chapterId is not null)
            {
                var artifact = await new FileSystemProductionScriptStore().LoadAsync(book, chapterId)
                    ?? throw new InvalidDataException("Attribute the current chapter before casting.");
                var chapter = book.Manuscript.Chapters.Single(chapter => chapter.Id == chapterId);
                if (!string.Equals(artifact.SourceSha256, new ChapterTextPreparer().Prepare(chapter).SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Current chapter attribution is stale; repeat attribution before casting.");
                requiredSpeakers = artifact.Script.Segments.Select(segment => segment.SpeakerId).Append("narrator").ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            if (autoVoices)
                await VoiceForgeProvisioning.EnsureAsync(book, libraryPath, verifiedVoiceLibrary, requiredSpeakers, chapterId,
                    Path.Combine(voiceForge, "provision.py"), voiceForgeImage, workerImage, whisper, ollamaUrl, model);
            if (chapterId is not null)
            {
                usage = await VoiceUsageContext.LoadAsync(book, chapterId,
                    new FileSystemProductionScriptStore(), await new FileSystemCastingPlanStore().LoadAsync(book));
                Console.WriteLine(usage is null
                    ? "Voice counts unavailable: attribution history is missing or stale."
                    : "Recording attributed speech counts. Voices remain reserved per character.");
            }
            var result = await workflow.ExecuteAsync(
                book,
                libraryPath,
                voiceUsage: usage,
                requiredSpeakers: requiredSpeakers);

            Console.WriteLine();
            Console.WriteLine("Automatic casting complete.");
            Console.WriteLine(
                $"Eligible voices: {result.EligibleVoiceCount}");
            Console.WriteLine(
                $"Assignments:     {result.AssignmentCount}");
            Console.WriteLine(
                $"Existing plan:   " +
                $"{(result.ReusedExistingPlan ? "reused" : "created")}");
            Console.WriteLine(
                $"Casting plan:    {Path.Combine(
                    book.RootPath,
                    "production",
                    "casting.json")}");

            return 0;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            IOException or
            HttpRequestException or
            UnauthorizedAccessException or
            UriFormatException or
            System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(
                $"Automatic casting failed: {exception.Message}");

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
