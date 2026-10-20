using System.Text.Json;
using StoryCast.Application.Attribution;

/// <summary>Book-level defaults shared by casting and chapter production.</summary>
internal static class AudioGenerationSettings
{
    internal static IReadOnlyList<DialogueConvention> DialogueConventions(string bookPath)
    {
        var path = Path.Combine(bookPath, "production", "storycast.json");
        if (!File.Exists(path)) return [];
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        if (!config.RootElement.TryGetProperty("dialogueConventions", out var rules)) return [];
        var result = new List<DialogueConvention>();
        foreach (var rule in rules.EnumerateArray())
        {
            var open = rule.GetProperty("open").GetString();
            var close = rule.GetProperty("close").GetString();
            var speaker = rule.TryGetProperty("speakerId", out var identity) ? identity.GetString() : null;
            if (open?.Length != 1 || close?.Length != 1 || char.IsWhiteSpace(open[0]) || char.IsWhiteSpace(close[0]) ||
                (speaker is not null && string.IsNullOrWhiteSpace(speaker)) || result.Any(existing => existing.Open == open[0]))
                throw new ArgumentException("Dialogue conventions require unique single-character opening delimiters, single-character closing delimiters, and valid speaker IDs.");
            result.Add(new DialogueConvention { Open = open[0], Close = close[0], SpeakerId = speaker,
                SpeakerLabelsOverride = rule.TryGetProperty("speakerLabelsOverride", out var labels) && labels.GetBoolean(),
                SpeakerIdsByLabel = rule.TryGetProperty("speakerIdsByLabel", out var overrides)
                    ? overrides.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>() });
        }
        return result;
    }

    internal static string? String(string bookPath, string name)
    {
        var path = Path.Combine(bookPath, "production", "storycast.json");
        if (!File.Exists(path)) return null;
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        return config.RootElement.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    }

    internal static bool AutoVoices(string bookPath, string[] args)
    {
        if (args.Contains("--no-auto-voices", StringComparer.OrdinalIgnoreCase)) return false;
        if (args.Contains("--auto-voices", StringComparer.OrdinalIgnoreCase)) return true;
        var path = Path.Combine(bookPath, "production", "storycast.json");
        if (!File.Exists(path)) return true;
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        return !config.RootElement.TryGetProperty("autoVoicesEnabled", out var value) || value.GetBoolean();
    }

    internal static string VoiceForgeRoot(string bookPath) => Path.GetFullPath(String(bookPath, "voiceForgeRoot") ??
        Environment.GetEnvironmentVariable("VOICEFORGE_REPOSITORY_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "VoiceForge"));
}
