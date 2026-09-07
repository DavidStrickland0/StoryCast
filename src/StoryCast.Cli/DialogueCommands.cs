using StoryCast.Application.Attribution;
using StoryCast.Application.Preparation;
using StoryCast.Infrastructure.Books;
using StoryCast.Infrastructure.Characters;
using StoryCast.Infrastructure.Production;
using StoryCast.Ollama;

internal static class DialogueCommands
{
    public static async Task<int> AttributeAsync(string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Dialogue attribution requires a book directory.");

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

            var ollamaUrl =
                GetOptionValue(args, "--ollama-url") ??
                "http://localhost:11434/";

            if (!ollamaUrl.EndsWith(
                    "/",
                    StringComparison.Ordinal))
            {
                ollamaUrl += "/";
            }

            var force = HasOption(
                args,
                "--force");

            return await ExecuteAsync(
                bookPath,
                model,
                ollamaUrl,
                force);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(
        string bookPath,
        string model,
        string ollamaUrl,
        bool force)
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

            var workflow =
                new DialogueAttributionWorkflow(
                    new ChapterTextPreparer(),
                    new ProductionScriptSegmenter(),
                    new DialogueAttributionService(generator),
                    new DialogueAttributionApplicator(),
                    new FileSystemCharacterRegistryStore(),
                    new FileSystemProductionScriptStore());

            Console.WriteLine($"Book:       {book.Title}");
            Console.WriteLine($"Book ID:    {book.Id}");
            Console.WriteLine($"Chapters:   {book.Manuscript.Chapters.Count}");
            Console.WriteLine($"Model:      {model}");
            Console.WriteLine($"Ollama:     {ollamaUrl}");
            Console.WriteLine();
            Console.WriteLine("Attributing dialogue...");

            var result = await workflow.ExecuteAsync(
                book,
                force: force);

            var scriptDirectory = Path.Combine(
                book.RootPath,
                "production",
                "scripts");

            Console.WriteLine();
            Console.WriteLine("Dialogue attribution complete.");
            Console.WriteLine(
                $"Processed:      {result.ProcessedChapters}");
            Console.WriteLine(
                $"Skipped:        {result.SkippedChapters}");
            Console.WriteLine(
                $"Dialogue lines: {result.DialogueSegments}");
            Console.WriteLine(
                $"Low confidence: {result.LowConfidenceAssignments}");
            Console.WriteLine(
                $"Scripts:        {scriptDirectory}");

            if (result.LowConfidenceAssignments > 0)
            {
                Console.WriteLine();
                Console.WriteLine(
                    "Review low-confidence assignments before synthesis.");
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            HttpRequestException or
            UnauthorizedAccessException or
            UriFormatException)
        {
            Console.Error.WriteLine(
                $"Dialogue attribution failed: {exception.Message}");

            return 1;
        }
    }

    private static bool HasOption(
        IReadOnlyList<string> args,
        string option)
    {
        return args.Any(
            argument => string.Equals(
                argument,
                option,
                StringComparison.OrdinalIgnoreCase));
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
