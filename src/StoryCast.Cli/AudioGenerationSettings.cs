using System.Text.Json;

/// <summary>Book-level defaults shared by casting and chapter production.</summary>
internal static class AudioGenerationSettings
{
    internal static string? String(string bookPath, string name)
    {
        var path = Path.Combine(bookPath, "production", "storycast.json");
        if (!File.Exists(path)) return null;
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        return config.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
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
