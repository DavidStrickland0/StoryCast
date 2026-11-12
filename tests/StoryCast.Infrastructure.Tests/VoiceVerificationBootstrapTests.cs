using System.Text.Json;
using StoryCast.Infrastructure.Voices;

namespace StoryCast.Infrastructure.Tests;

/// <summary>Checks first-run casting without weakening speech verification.</summary>
public sealed class VoiceVerificationBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"voice-bootstrap-{Guid.NewGuid():N}");

    /// <summary>Missing verification permits provisioning, but never permits casting old samples.</summary>
    [Fact]
    public async Task MissingReportReturnsNoEligibleVoicesAndKeepsStrictMode()
    {
        await this.AddVoice("old");
        var eligible = new FileSystemVerifiedVoiceLibrary(new FileSystemVoiceLibrary(), allowUnverifiedEntries: true);
        Assert.Empty(await eligible.LoadAsync(this.root));
        Assert.False(File.Exists(Path.Combine(this.root, "voice-verification.json")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new FileSystemVerifiedVoiceLibrary(new FileSystemVoiceLibrary()).LoadAsync(this.root));
    }

    /// <summary>A newly verified voice can coexist with old unverified and failed samples.</summary>
    [Fact]
    public async Task PartialReportSelectsOnlyPassingVoicesAndRejectsCorruptReports()
    {
        foreach (var id in new[] { "old", "verified", "failed" }) await this.AddVoice(id);
        var path = Path.Combine(this.root, "voice-verification.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, voices = new[] {
            new { voiceId = "verified", status = "pass" }, new { voiceId = "failed", status = "fail" } } }));
        var eligible = new FileSystemVerifiedVoiceLibrary(new FileSystemVoiceLibrary(), allowUnverifiedEntries: true);
        Assert.Equal("verified", Assert.Single(await eligible.LoadAsync(this.root)).Id);
        await File.WriteAllTextAsync(path, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => eligible.LoadAsync(this.root));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(this.root)) Directory.Delete(this.root, recursive: true);
    }

    private async Task AddVoice(string id)
    {
        var folder = Path.Combine(this.root, id);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "sample.wav"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(folder, "voice.json"), JsonSerializer.Serialize(new { id, sample = "sample.wav", language = "en" }));
    }
}
