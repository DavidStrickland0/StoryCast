using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Starts the local reusable ambience service for automatic chapter production.</summary>
internal static class SoundForgeRuntime
{
    internal sealed record Settings(bool Enabled, string WorkerUrl, double GainDb);

    internal static Settings ReadSettings(string bookPath, string[] args)
    {
        var path = Path.Combine(bookPath, "production", "storycast.json");
        using var configuration = JsonDocument.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
        var root = configuration.RootElement;
        bool enabled = !args.Contains("--no-ambience", StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("ambienceEnabled", out var flag)) enabled &= flag.GetBoolean();
        string? Option(string name)
        {
            var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }
        var url = Option("--soundforge-url") ?? Environment.GetEnvironmentVariable("SOUNDFORGE_URL") ??
            (root.TryGetProperty("soundforgeUrl", out var endpoint) ? endpoint.GetString() : null) ?? "http://host.docker.internal:7861";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new ArgumentException("SoundForge URL must be an absolute HTTP or HTTPS URL.");
        var gain = Option("--ambience-gain-db") is { } value
            ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : root.TryGetProperty("ambienceGainDb", out var volume) ? volume.GetDouble() : -22;
        if (!double.IsFinite(gain) || gain is < -40 or > -12)
            throw new ArgumentException("Ambience gain must be between -40 and -12 dB.");
        return new Settings(enabled, url.TrimEnd('/'), gain);
    }

    internal static async Task EnsureAsync(Settings settings)
    {
        if (!settings.Enabled) return;
        var endpoint = new Uri(settings.WorkerUrl);
        var local = endpoint.Host is "host.docker.internal" or "localhost" or "127.0.0.1";
        var serviceHost = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "host.docker.internal" : "localhost";
        var hostUrl = local ? new UriBuilder(endpoint) { Host = serviceHost }.Uri.ToString().TrimEnd('/') : settings.WorkerUrl;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        async Task<bool> Healthy()
        {
            try
            {
                using var health = await client.GetFromJsonAsync<JsonDocument>(hostUrl + "/api/health");
                return health?.RootElement.GetProperty("service").GetString() == "SoundForge";
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                return false;
            }
        }
        if (await Healthy()) return;
        if (!local || endpoint.Port != 7861)
            throw new IOException($"SoundForge is unavailable at {hostUrl}. Start that service or use --no-ambience.");
        var inferredRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "SoundForge"));
        if (!File.Exists(Path.Combine(inferredRoot, "app.py")))
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "SoundForge");
                if (File.Exists(Path.Combine(candidate, "app.py"))) { inferredRoot = candidate; break; }
            }
        }
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("SOUNDFORGE_REPOSITORY_ROOT") ?? inferredRoot);
        if (!File.Exists(Path.Combine(root, "app.py")))
            throw new IOException($"SoundForge was not found at {root}. Set SOUNDFORGE_REPOSITORY_ROOT.");
        var library = Path.GetFullPath(Environment.GetEnvironmentVariable("SOUNDFORGE_LIBRARY") ?? Path.Combine(root, "library"));
        var previews = Path.Combine(root, "data");
        var localModel = Path.GetFullPath(Environment.GetEnvironmentVariable("SOUNDFORGE_MODEL_DIRECTORY") ?? Path.Combine(root, "models", "small-sfx"));
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(previews);
        Directory.CreateDirectory(localModel);
        async Task<(int Code, string Output)> Docker(params string[] arguments)
        {
            var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start Docker for SoundForge.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await errors);
        }
        var existing = await Docker("inspect", "--format", "{{.State.Running}}", "soundforge");
        if (existing.Code == 0)
        {
            if (existing.Output.Trim() == "false")
            {
                var started = await Docker("start", "soundforge");
                if (started.Code != 0) throw new IOException(started.Output);
            }
        }
        else
        {
            Console.WriteLine($"Starting SoundForge. Library: {library}");
            var device = Environment.GetEnvironmentVariable("SOUNDFORGE_DEVICE") ?? "cuda";
            var command = new List<string> { "run", "--detach", "--name", "soundforge", "--restart", "unless-stopped",
                "-p", "127.0.0.1:7861:7861", "--env", $"SOUNDFORGE_DEVICE={device}",
                "--env", $"SOUNDFORGE_HOST_ROOT={root}",
                "--mount", $"type=bind,source={library},target=/library", "--mount", $"type=bind,source={previews},target=/data",
                "--mount", $"type=bind,source={localModel},target=/local-model,readonly",
                "--mount", "type=volume,source=soundforge-models,target=/models" };
            if (device == "cuda") command.AddRange(["--gpus", "all"]);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HF_TOKEN"))) command.AddRange(["--env", "HF_TOKEN"]);
            command.Add(Environment.GetEnvironmentVariable("SOUNDFORGE_IMAGE") ?? "soundforge:dev");
            var started = await Docker(command.ToArray());
            if (started.Code != 0) throw new IOException("SoundForge startup failed. Build SoundForge/run.ps1 -Build first. " + started.Output);
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await Healthy()) return;
            await Task.Delay(1000);
        }
        throw new IOException($"SoundForge did not become ready at {hostUrl}. Inspect docker logs soundforge.");
    }

    internal static string PreferredAudio(string runDirectory, string chapterId, bool ambienceEnabled)
    {
        var folder = Path.Combine(runDirectory, chapterId);
        var mixed = Path.Combine(folder, $"{chapterId}.ambience.wav");
        return ambienceEnabled && File.Exists(mixed) ? mixed : Path.Combine(folder, $"{chapterId}.mastered.wav");
    }
}
