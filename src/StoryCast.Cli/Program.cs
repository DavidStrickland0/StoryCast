using StoryCast.Application.Manuscripts;
using StoryCast.Application.Voices;
using StoryCast.Infrastructure.Manuscripts;
using StoryCast.Infrastructure.Voices;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || HasOption(args, "--help", "-h"))
    {
        WriteHelp();
        return 0;
    }

    if (args.Length >= 2 &&
        EqualsArgument(args[0], "voices") &&
        EqualsArgument(args[1], "list"))
    {
        return await ListVoicesAsync(args[2..]);
    }

    if (args.Length >= 2 &&
        EqualsArgument(args[0], "manuscript") &&
        EqualsArgument(args[1], "inspect"))
    {
        return await InspectManuscriptAsync(args[2..]);
    }

    if (args.Length >= 2 &&
        EqualsArgument(args[0], "characters") &&
        EqualsArgument(args[1], "discover"))
    {
        return await CharacterCommands.DiscoverAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "dialogue") &&
        EqualsArgument(args[1], "attribute"))
    {
        return await DialogueCommands.AttributeAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "voices") &&
        EqualsArgument(args[1], "enrich"))
    {
        return await VoiceCommands.EnrichAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "voices") &&
        EqualsArgument(args[1], "analyze"))
    {
        return await VoiceCommands.AnalyzeAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "voices") &&
        EqualsArgument(args[1], "verify"))
    {
        return await VoiceVerificationCommands.VerifyAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "cast") &&
        EqualsArgument(args[1], "assign"))
    {
        return await CastingCommands.AssignAsync(args[2..]);
    }
    if (args.Length >= 2 &&
        EqualsArgument(args[0], "produce") &&
        EqualsArgument(args[1], "book"))
    {
        return await BookProductionCommands.ProduceBookAsync(
            args[2..]);
    }

    if (args.Length >= 2 &&
        EqualsArgument(args[0], "produce") &&
        EqualsArgument(args[1], "chapter"))
    {
        return await ProductionCommands.ProduceChapterAsync(
            args[2..]);
    }
    Console.Error.WriteLine(
        $"Unknown command: {string.Join(' ', args)}");

    Console.Error.WriteLine();
    WriteHelp();

    return 1;
}

static async Task<int> ListVoicesAsync(string[] args)
{
    string libraryPath;

    try
    {
        libraryPath = GetOptionValue(
            args,
            "--library") ?? Path.Combine(
                Directory.GetCurrentDirectory(),
                "voices");
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    IVoiceLibrary voiceLibrary = new FileSystemVoiceLibrary();

    try
    {
        var voices = await voiceLibrary.LoadAsync(libraryPath);

        Console.WriteLine(
            $"Voice library: {Path.GetFullPath(libraryPath)}");

        Console.WriteLine($"Voices: {voices.Count}");

        if (voices.Count == 0)
        {
            Console.WriteLine("No voices were found.");
            return 0;
        }

        Console.WriteLine();

        foreach (var voice in voices.OrderBy(
                     voice => voice.Id,
                     StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(voice.Id);
            Console.WriteLine($"  Sample:       {voice.SamplePath}");
            Console.WriteLine($"  Language:     {voice.Language}");
            Console.WriteLine($"  Accent:       {DisplayText(voice.Accent)}");
            Console.WriteLine(
                $"  Apparent age: {DisplayText(voice.ApparentAge)}");
            Console.WriteLine(
                $"  Presentation: {DisplayText(voice.Presentation)}");
            Console.WriteLine(
                $"  Qualities:    {DisplayList(voice.Qualities)}");
            Console.WriteLine(
                $"  Roles:        {DisplayList(voice.SuitableRoles)}");
            Console.WriteLine(
                $"  Narrator:     {(voice.NarratorSuitable ? "yes" : "no")}");
            Console.WriteLine();
        }

        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        System.Text.Json.JsonException)
    {
        Console.Error.WriteLine(
            $"Unable to load voice library: {exception.Message}");

        return 1;
    }
}

static async Task<int> InspectManuscriptAsync(string[] args)
{
    if (args.Length == 0 ||
        args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "The manuscript inspect command requires a file or directory.");

        return 1;
    }

    var sourcePath = args[0];

    IManuscriptLoader loader =
        new FileSystemManuscriptLoader();

    try
    {
        var manuscript = await loader.LoadAsync(sourcePath);

        var totalCharacters = manuscript.Chapters.Sum(
            chapter => chapter.RawText.Length);

        var totalWords = manuscript.Chapters.Sum(
            chapter => CountWords(chapter.RawText));

        Console.WriteLine(
            $"Manuscript: {manuscript.SourcePath}");

        Console.WriteLine(
            $"Chapters:   {manuscript.Chapters.Count}");

        Console.WriteLine(
            $"Characters: {totalCharacters:N0}");

        Console.WriteLine(
            $"Words:      {totalWords:N0}");

        Console.WriteLine(
            $"Estimated:  {FormatDuration(totalWords)} at 150 words/minute");

        Console.WriteLine();

        foreach (var chapter in manuscript.Chapters)
        {
            var words = CountWords(chapter.RawText);

            Console.WriteLine(
                $"{chapter.Id}  {chapter.FileName}");

            Console.WriteLine(
                $"  Format:     {chapter.Format}");

            Console.WriteLine(
                $"  Characters: {chapter.RawText.Length:N0}");

            Console.WriteLine(
                $"  Words:      {words:N0}");

            Console.WriteLine(
                $"  Estimated:  {FormatDuration(words)}");

            Console.WriteLine(
                $"  Source:     {chapter.SourcePath}");

            Console.WriteLine();
        }

        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"Unable to load manuscript: {exception.Message}");

        return 1;
    }
}

static int CountWords(string text)
{
    return text.Split(
        [' ', '\t', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries).Length;
}

static string FormatDuration(int wordCount)
{
    var seconds = wordCount / 150.0 * 60.0;
    var duration = TimeSpan.FromSeconds(seconds);

    if (duration.TotalHours >= 1)
    {
        return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    return $"{duration.Minutes}:{duration.Seconds:00}";
}

static string? GetOptionValue(
    IReadOnlyList<string> args,
    string option)
{
    for (var index = 0; index < args.Count; index++)
    {
        if (!EqualsArgument(args[index], option))
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

static bool HasOption(
    IEnumerable<string> args,
    params string[] options)
{
    return args.Any(
        argument => options.Contains(
            argument,
            StringComparer.OrdinalIgnoreCase));
}

static bool EqualsArgument(
    string value,
    string expected)
{
    return string.Equals(
        value,
        expected,
        StringComparison.OrdinalIgnoreCase);
}

static string DisplayText(string value)
{
    return string.IsNullOrWhiteSpace(value)
        ? "(unspecified)"
        : value;
}

static string DisplayList(IReadOnlyList<string> values)
{
    return values.Count == 0
        ? "(none)"
        : string.Join(", ", values);
}

static void WriteHelp()
{
    Console.WriteLine(
        """
        StoryCast

        Usage:
          storycast voices list [--library <path>]
          storycast voices enrich [--library <path>]
          storycast voices analyze [--library <path>] [--output <path>]
          storycast voices verify [--library <path>] [--model <model>]
          storycast manuscript inspect <file-or-directory>
          storycast characters discover <book-directory> --model <model>
          storycast dialogue attribute <book-directory> --model <model>
          storycast cast assign <book-directory> --model <model> [--library <path>]
          storycast produce chapter <book-directory> <chapter-id> [--library <path>] [--resume-run <path>]
          storycast produce book <book-directory> [--library <path>] [--resume-book-run <path>]

        Commands:
          voices list         Validate and display available voices.
          voices enrich       Add metadata derived from voice directories.
          voices analyze      Measure voice-sample audio quality.
          voices verify       Verify synthesized speech with Whisper.
          manuscript inspect  Display manuscript chapters and statistics.
          characters discover  Discover and persist speaking characters.
          dialogue attribute    Assign every dialogue line to a character.
          cast assign         Assign verified voices to audiobook roles.
          produce chapter     Synthesize, verify, assemble, and master a chapter.
          produce book        Produce every configured chapter in order.

        Options:
          --library      Voice-library directory. Defaults to .\voices.
          --model        Installed Ollama model used for analysis.
          --ollama-url   Ollama URL. Defaults to http://localhost:11434/.
          --force       Reprocess unchanged chapters.
          --resume-run        Continue an incomplete chapter run.
          --resume-book-run   Continue an incomplete book run.
          --help, -h     Display this help.
        """);
}
