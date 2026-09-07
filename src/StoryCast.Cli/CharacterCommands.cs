using StoryCast.Application.Attribution;
using StoryCast.Application.Characters;
using StoryCast.Application.Preparation;
using StoryCast.Infrastructure.Books;
using StoryCast.Infrastructure.Characters;
using StoryCast.Ollama;

internal static class CharacterCommands
{
    public static async Task<int> DiscoverAsync(string[] args)
    {
        if (args.Length == 0 ||
            args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Character discovery requires a book directory.");

            return 1;
        }

        var bookPath = args[0];


        var force = args.Any(

            argument => string.Equals(

                argument,

                "--force",

                StringComparison.OrdinalIgnoreCase));
        string model;

        try
        {
            model =
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
                new CharacterDiscoveryWorkflow(
                    new ChapterTextPreparer(),
                    new CharacterDiscoveryService(generator),
                    new CharacterRegistryMerger(),
                    new FileSystemCharacterRegistryStore());

            Console.WriteLine($"Book:       {book.Title}");
            Console.WriteLine($"Book ID:    {book.Id}");
            Console.WriteLine($"Chapters:   {book.Manuscript.Chapters.Count}");
            Console.WriteLine($"Model:      {model}");
            Console.WriteLine($"Ollama:     {ollamaUrl}");
            Console.WriteLine($"Force:      {(force ? "yes" : "no")}");
            Console.WriteLine();
            Console.WriteLine("Discovering characters...");

            var result = await workflow.ExecuteAsync(
                book,
                force: force);

            Console.WriteLine();
            Console.WriteLine("Character discovery complete.");
            Console.WriteLine(
                $"Processed:  {result.ProcessedChapters}");
            Console.WriteLine(
                $"Skipped:    {result.SkippedChapters}");
            Console.WriteLine(
                $"Characters: {result.CharacterCount}");
            Console.WriteLine(
                $"Registry:   {Path.Combine(
                    book.RootPath,
                    "production",
                    "characters.json")}");

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            HttpRequestException or
            UnauthorizedAccessException or
            UriFormatException)
        {
            Console.Error.WriteLine(
                $"Character discovery failed: {exception.Message}");

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
