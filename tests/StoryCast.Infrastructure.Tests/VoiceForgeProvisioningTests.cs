using System.Security.Cryptography;
using System.Text.Json;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Infrastructure.Tests;

public sealed class VoiceForgeProvisioningTests
{
    private static CharacterProfile Maren() => new() { Id = "maren", DisplayName = "Maren", VoicePresentation = "female", VoiceTraits = ["gruff", "clipped"] };
    private static VoiceProfile Voice(string presentation = "female", params string[] traits) => new() { Id = "voice", SamplePath = "sample.wav", Presentation = presentation, Qualities = traits };

    [Fact]
    public void StockVoiceWithoutAttributesIsNotAValidMatch()
    {
        Assert.False(VoiceForgeProvisioning.AttributesMatch(Maren(), Voice("female")));
        Assert.False(VoiceForgeProvisioning.AttributesMatch(Maren(), Voice("female", "gruff")));
        Assert.False(VoiceForgeProvisioning.AttributesMatch(Maren(), Voice("male", "gruff", "clipped")));
        Assert.True(VoiceForgeProvisioning.AttributesMatch(Maren(), Voice("female", " Gruff ", "CLIPPED")));
    }

    [Fact]
    public void ExistingFallbackIsReevaluatedButValidCastIsRetained()
    {
        var assignments = new[] { new CastingAssignment { CharacterId = "maren", VoiceId = "voice" } };
        Assert.Single(VoiceForgeProvisioning.RolesNeedingVoices([Maren()], assignments, [Voice("female")]));
        Assert.Empty(VoiceForgeProvisioning.RolesNeedingVoices([Maren()], assignments, [Voice("female", "gruff", "clipped")]));
        Assert.Throws<InvalidDataException>(() => VoiceForgeProvisioning.RolesNeedingVoices([Maren()],
            [new() { CharacterId = "maren", VoiceId = "voice", IsLocked = true }], [Voice("female")]));
    }

    [Fact]
    public void NarratorRequiresNarrationSuitabilityAndAttributes()
    {
        var role = new CharacterProfile { Id = "narrator", DisplayName = "Narrator", VoiceTraits = ["clear", "steady"] };
        Assert.False(VoiceForgeProvisioning.AttributesMatch(role, Voice("female", "clear", "steady")));
        Assert.True(VoiceForgeProvisioning.AttributesMatch(role, new() { Id = "n", SamplePath = "s.wav", NarratorSuitable = true, Qualities = ["clear", "steady"] }));
    }

    [Fact]
    public void AutomaticProvisioningIsTheDefaultAndCanBeDisabledExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.True(AudioGenerationSettings.AutoVoices(root, []));
        Assert.False(AudioGenerationSettings.AutoVoices(root, ["--no-auto-voices"]));
    }

    [Fact]
    public async Task ImportRequiresBothAuditionsAndUnchangedVerifiedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(root, "candidate");
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(candidate, "sample.wav"), "sample");
        await File.WriteAllTextAsync(Path.Combine(candidate, "voice.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(candidate, "audition.wav"), "audition");
        var identity = "voiceforge-" + new string('a', 32);
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(candidate, file)))).ToLowerInvariant();
        async Task Report(string audition) => await File.WriteAllTextAsync(Path.Combine(candidate, "generated-verification.json"),
            JsonSerializer.Serialize(new { voiceId = identity, status = "pass", reference = new { status = "pass" },
                audition = new { status = audition }, sampleSha256 = Hash("sample.wav"), manifestSha256 = Hash("voice.json") }));
        await Report("fail");
        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceForgeProvisioning.ImportVerifiedAsync(candidate, library, identity));
        Assert.Empty(Directory.GetDirectories(library));
        await Report("pass");
        await File.WriteAllTextAsync(Path.Combine(candidate, "sample.wav"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceForgeProvisioning.ImportVerifiedAsync(candidate, library, identity));
        await Report("pass");
        await VoiceForgeProvisioning.ImportVerifiedAsync(candidate, library, identity);
        Assert.True(File.Exists(Path.Combine(library, identity, "sample.wav")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(library, "voice-verification.json")));
        Assert.Equal("pass", report.RootElement.GetProperty("voices")[0].GetProperty("status").GetString());
        Directory.Delete(root, true);
    }
}
