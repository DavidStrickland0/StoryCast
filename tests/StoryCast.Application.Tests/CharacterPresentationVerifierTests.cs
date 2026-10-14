using System.Text.Json;
using StoryCast.Application.Attribution;
using StoryCast.Application.TextGeneration;
using StoryCast.Domain.Characters;

namespace StoryCast.Application.Tests.Attribution;

public sealed class CharacterPresentationVerifierTests
{
    [Fact]
    public async Task IndependentlyChecksEveryIdentityWithoutSeeingProposedPresentations()
    {
        var generator = new Generator("""
            {"characters":[{"id":"maren","presentation":"female","evidence":["Maren lifted her hammer."]},
            {"id":"torin","presentation":"male","evidence":["Torin drew his sword."]}]}
            """);
        var result = await new CharacterPresentationVerifier(generator).VerifyAsync(
            "Maren lifted her hammer. Torin drew his sword.",
            [new() { Id = "maren", DisplayName = "Maren", VoicePresentation = "male" },
             new() { Id = "torin", DisplayName = "Torin", VoicePresentation = "unspecified" }]);
        Assert.Equal("female", result["maren"]);
        Assert.Equal("male", result["torin"]);
        Assert.DoesNotContain("VoicePresentation", generator.Prompt);
    }

    [Theory]
    [InlineData("Maren lowered her hammer.")]
    [InlineData("Torin drew his sword.")]
    [InlineData("Maren lifted her hammer.")]
    [InlineData("Maren followed Torin, his sword drawn and her hammer raised.")]
    public async Task RejectsInventedWrongIdentityOrWrongPresentationEvidence(string evidence)
    {
        var generator = new Generator(JsonSerializer.Serialize(new { characters = new[] {
            new { id = "maren", presentation = "male", evidence = new[] { evidence } } } }));
        var result = await new CharacterPresentationVerifier(generator).VerifyAsync(
            "Maren lifted her hammer. Torin drew his sword. Maren followed Torin, his sword drawn and her hammer raised.",
            [new() { Id = "maren", DisplayName = "Maren" }]);
        Assert.Equal("unspecified", result["maren"]);
        Assert.Equal(3, generator.Calls);
    }

    [Fact]
    public async Task MissingEvidenceCanBeCorrectedAutomatically()
    {
        var generator = new Generator(
            """{"characters":[{"id":"maren","presentation":"female","evidence":[]}]}""",
            """{"characters":[{"id":"maren","presentation":"female","evidence":["Maren lifted her hammer."]}]}""");
        var result = await new CharacterPresentationVerifier(generator).VerifyAsync(
            "Maren lifted her hammer.", [new() { Id = "maren", DisplayName = "Maren" }]);
        Assert.Equal("female", result["maren"]);
        Assert.Equal(2, generator.Calls);
        Assert.Contains("Rejected evidence", generator.Prompt);
    }

    private sealed class Generator(params string[] responses) : IStructuredTextGenerator
    {
        public int Calls { get; private set; }
        public string Prompt { get; private set; } = "";
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, JsonElement jsonSchema,
            CancellationToken cancellationToken = default)
        {
            Prompt = userPrompt;
            return Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
        }
    }
}
