using System.Diagnostics;
using System.Text.Json;

/// <summary>Start the local Docker image renderer for books with chapter art enabled.</summary>
internal static class IllustrationRuntime
{
    internal static async Task EnsureAsync(string bookPath)
    {
        var path = Path.Combine(bookPath, "production", "illustrations.json");
        if (!File.Exists(path)) return;
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        if (!config.RootElement.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) return;
        var url = config.RootElement.TryGetProperty("rendererUrl", out var endpoint)
            ? endpoint.GetString() ?? "http://host.docker.internal:7862" : "http://host.docker.internal:7862";
        var address = new Uri(url);
        if (address.Host is not ("localhost" or "127.0.0.1" or "host.docker.internal") || address.Port != 7862) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                using var health = await client.GetAsync(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
                    ? "http://host.docker.internal:7862/health" : "http://localhost:7862/health");
                if (health.IsSuccessStatusCode) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            async Task<(int Code, string Output)> Docker(params string[] arguments)
            {
                var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Could not start Docker.");
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, await output + await errors);
            }
            var exists = await Docker("inspect", "--format", "{{.State.Running}}", "storycast-illustrations");
            var result = exists.Code == 0
                ? await Docker("start", "storycast-illustrations")
                : await Docker("run", "--detach", "--name", "storycast-illustrations", "--restart", "unless-stopped",
                    "--gpus", "all", "-p", "127.0.0.1:7862:7862", "--mount", "type=volume,source=storycast-illustration-models,target=/models",
                    "storycast-illustrations:dev");
            if (result.Code != 0) throw new IOException(result.Output);
            Console.WriteLine("Local Docker chapter illustration renderer started.");
        }
        catch (Exception error)
        {
            // Narration remains producible; chapter_art persists a retryable artwork finding.
            Console.Error.WriteLine($"Chapter artwork renderer unavailable; artwork can be retried: {error.Message}");
        }
    }
}
