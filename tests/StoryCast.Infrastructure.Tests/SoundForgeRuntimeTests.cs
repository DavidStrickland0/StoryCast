using System.Text.Json;

namespace StoryCast.Infrastructure.Tests;

/// <summary>Tests default ambience settings, opt-out behavior, and host path display.</summary>
public sealed class SoundForgeRuntimeTests
{
    /// <summary>Production automatically enables quiet background ambience.</summary>
    [Fact]
    public void DefaultProductionEnablesAmbience()
    {
        var settings = SoundForgeRuntime.ReadSettings(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), []);
        Assert.True(settings.Enabled);
        Assert.Equal(-22, settings.GainDb);
        Assert.EndsWith(":7861", settings.WorkerUrl);
    }

    /// <summary>Disabled ambience never launches Docker or contacts the service.</summary>
    [Fact]
    public async Task ExplicitOptOutDoesNotStartAService()
    {
        var settings = SoundForgeRuntime.ReadSettings(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ["--no-ambience"]);
        Assert.False(settings.Enabled);
        await SoundForgeRuntime.EnsureAsync(settings);
    }

    /// <summary>Story settings are retained while explicit endpoint and volume options override defaults.</summary>
    [Fact]
    public void StorySettingsAndCommandOverridesAreHonored()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "production"));
            File.WriteAllText(Path.Combine(root, "production", "storycast.json"),
                JsonSerializer.Serialize(new { ambienceEnabled = false, ambienceGainDb = -30, soundforgeUrl = "http://localhost:7861" }));
            var defaults = SoundForgeRuntime.ReadSettings(root, []);
            Assert.False(defaults.Enabled);
            Assert.Equal(-30, defaults.GainDb);
            var overridden = SoundForgeRuntime.ReadSettings(root, ["--ambience-gain-db", "-24", "--soundforge-url", "http://localhost:9000"]);
            Assert.Equal(-24, overridden.GainDb);
            Assert.Equal("http://localhost:9000", overridden.WorkerUrl);
            Assert.False(overridden.Enabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Invalid or excessive gain cannot mask narration.</summary>
    /// <param name="value">The invalid requested gain.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("-100")]
    public void ExcessiveOrInvalidBackgroundLevelsAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => SoundForgeRuntime.ReadSettings(Path.GetTempPath(), ["--ambience-gain-db", value]));
    }

    /// <summary>Displayed container paths resolve to the host's actual files.</summary>
    [Fact]
    public void ContainerPathsInOutputBecomeAbsoluteHostPaths()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "book"));
        var voices = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "voices"));
        var runtime = new DockerWorkerRuntime(root, voices);
        Assert.Equal($"MP3: {root.Replace('\\', '/')}/output/final/chapter-001.mp3", runtime.ToHostOutput("MP3: /books/output/final/chapter-001.mp3"));
        Assert.Equal($"Voice: {voices.Replace('\\', '/')}/voice/sample.wav", runtime.ToHostOutput("Voice: /voices/voice/sample.wav"));
        Assert.Equal("/bookshelf is unrelated", runtime.ToHostOutput("/bookshelf is unrelated"));
    }

    /// <summary>Book production keeps the mixed soundtrack when ambience is enabled.</summary>
    [Fact]
    public void BookProductionPrefersAmbienceWhileKeepingCleanAudioAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "chapter-001");
        try
        {
            Directory.CreateDirectory(folder);
            var clean = Path.Combine(folder, "chapter-001.mastered.wav");
            var mixed = Path.Combine(folder, "chapter-001.ambience.wav");
            File.WriteAllText(clean, "clean fixture");
            Assert.Equal(clean, SoundForgeRuntime.PreferredAudio(root, "chapter-001", true));
            File.WriteAllText(mixed, "mixed fixture");
            Assert.Equal(mixed, SoundForgeRuntime.PreferredAudio(root, "chapter-001", true));
            Assert.Equal(clean, SoundForgeRuntime.PreferredAudio(root, "chapter-001", false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
