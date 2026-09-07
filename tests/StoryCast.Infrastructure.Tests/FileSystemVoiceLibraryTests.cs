using StoryCast.Infrastructure.Voices;

namespace StoryCast.Infrastructure.Tests.Voices;

/// <summary>
/// Tests filesystem voice-library discovery and validation.
/// </summary>
public sealed class FileSystemVoiceLibraryTests
{
    /// <summary>
    /// Verifies that a valid voice manifest and sample are loaded.
    /// </summary>
    [Fact]
    public async Task LoadAsync_LoadsValidVoiceProfile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"storycast-tests-{Guid.NewGuid():N}");

        var voiceDirectory = Path.Combine(root, "commander");

        try
        {
            Directory.CreateDirectory(voiceDirectory);

            await File.WriteAllBytesAsync(
                Path.Combine(voiceDirectory, "sample.wav"),
                [1, 2, 3]);

            await File.WriteAllTextAsync(
                Path.Combine(voiceDirectory, "voice.json"),
                """
                {
                  "id": "commander-01",
                  "sample": "sample.wav",
                  "language": "en",
                  "accent": "general-american",
                  "apparentAge": "45-60",
                  "presentation": "masculine",
                  "qualities": [
                    "authoritative",
                    "restrained"
                  ],
                  "suitableRoles": [
                    "commander",
                    "soldier"
                  ],
                  "narratorSuitable": false
                }
                """);

            var library = new FileSystemVoiceLibrary();

            var voices = await library.LoadAsync(root);

            var voice = Assert.Single(voices);

            Assert.Equal("commander-01", voice.Id);
            Assert.Equal("en", voice.Language);
            Assert.Equal("general-american", voice.Accent);
            Assert.Contains("authoritative", voice.Qualities);
            Assert.EndsWith("sample.wav", voice.SamplePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
