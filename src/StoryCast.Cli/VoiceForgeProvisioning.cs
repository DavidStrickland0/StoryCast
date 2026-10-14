using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StoryCast.Application.Voices;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;
using StoryCast.Infrastructure.Casting;
using StoryCast.Infrastructure.Characters;
using StoryCast.Infrastructure.Voices;

internal static class VoiceForgeProvisioning
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal static bool AttributesMatch(CharacterProfile role, VoiceProfile voice)
    {
        static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (role.VoicePresentation is "male" or "female" && !string.Equals(role.VoicePresentation, voice.Presentation, StringComparison.OrdinalIgnoreCase)) return false;
        if (role.Id == "narrator" && !voice.NarratorSuitable) return false;
        var qualities = voice.Qualities.Select(Normalize).ToHashSet();
        var traits = role.VoiceTraits.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalize).ToArray();
        return traits.Length == 0 ? qualities.Count > 0 || voice.SuitableRoles.Contains(role.Id, StringComparer.OrdinalIgnoreCase) : traits.All(qualities.Contains);
    }

    internal static IReadOnlyList<CharacterProfile> RequiredRoles(CharacterRegistry registry, IReadOnlySet<string>? requiredSpeakers)
    {
        var roles = registry.Characters.Where(role => requiredSpeakers is null || requiredSpeakers.Contains(role.Id)).ToList();
        roles.Add(new CharacterProfile { Id = "narrator", DisplayName = "Narrator", Description = "Clear, steady voice for sustained book narration.", VoiceTraits = ["clear", "steady"], IsNarrator = true });
        return roles;
    }

    internal static IReadOnlyList<CharacterProfile> RolesNeedingVoices(IReadOnlyList<CharacterProfile> roles, IReadOnlyList<CastingAssignment> assignments, IReadOnlyList<VoiceProfile> voices)
    {
        var byVoice = voices.ToDictionary(voice => voice.Id, StringComparer.OrdinalIgnoreCase);
        return roles.Where(role => {
            var assigned = assignments.SingleOrDefault(item => item.CharacterId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
            var matches = assigned is not null && byVoice.TryGetValue(assigned.VoiceId, out var voice) && AttributesMatch(role, voice);
            if (!matches && assigned?.IsLocked == true) throw new InvalidDataException($"Locked casting for '{role.Id}' does not match required attributes. Unlock that assignment to recast it.");
            return !matches;
        }).ToArray();
    }

    internal static async Task EnsureAsync(BookProject book, string library, IVerifiedVoiceLibrary verifiedLibrary,
        IReadOnlySet<string>? requiredSpeakers, string? chapterId, string script, string image, string workerImage, string whisper,
        string ollamaUrl, string model, bool allowGeneration = true)
    {
        library = Path.GetFullPath(library);
        var store = new FileSystemCastingPlanStore();
        var plan = await store.LoadAsync(book);
        var registry = await new FileSystemCharacterRegistryStore().LoadAsync(book) ?? throw new InvalidDataException("Discover characters before requesting voices.");
        var assignments = plan?.Assignments ?? [];
        Directory.CreateDirectory(library);
        IReadOnlyList<VoiceProfile> voices = [];
        if ((await new FileSystemVoiceLibrary().LoadAsync(library)).Count > 0)
        {
            try { voices = await verifiedLibrary.LoadAsync(library); }
            catch (InvalidDataException error) when (error.Message == "No voices passed speech verification.") { }
        }
        var roles = RolesNeedingVoices(RequiredRoles(registry, requiredSpeakers), assignments, voices);
        if (roles.Count == 0) return;
        if (!allowGeneration) throw new InvalidDataException($"Matching attributed voices required for: {string.Join(", ", roles.Select(role => role.Id))}. Automatic provisioning is disabled.");
        var root = Path.Combine(book.RootPath, "production", "voiceforge");
        Directory.CreateDirectory(root);
        await WriteJsonAsync(Path.Combine(root, "casting-request.json"), new { bookId = book.Id, chapterId,
            roles = roles.Select(role => new { roleId = role.Id, displayName = role.DisplayName,
                description = string.IsNullOrWhiteSpace(role.Description) ? $"A clear, natural voice for {role.DisplayName}." : role.Description,
                presentation = role.VoicePresentation, qualities = role.VoiceTraits, accent = "", age = "" }) });
        var reserved = assignments.Concat(plan?.RetiredAssignments ?? []).Select(item => item.VoiceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacements = new List<CastingAssignment>();
        foreach (var role in roles.OrderByDescending(role => role.VoiceTraits.Count))
        {
            var match = voices.FirstOrDefault(voice => !reserved.Contains(voice.Id) && AttributesMatch(role, voice));
            if (match is null)
            {
                Console.WriteLine($"VoiceForge: no verified attributed match for '{role.Id}' ({string.Join(", ", role.VoiceTraits)}); designing a voice.");
                match = await GenerateAsync(role, root, library, Path.GetDirectoryName(Path.GetFullPath(script))!, image, workerImage, whisper);
                voices = await verifiedLibrary.LoadAsync(library);
            }
            else Console.WriteLine($"VoiceForge: reusing attributed voice '{match.Id}' for '{role.Id}'.");
            if (!AttributesMatch(role, match)) throw new InvalidDataException($"Voice '{match.Id}' lacks required attributes for '{role.Id}'.");
            reserved.Add(match.Id);
            replacements.Add(new CastingAssignment { CharacterId = role.Id, VoiceId = match.Id, Confidence = .8m,
                Synthesis = assignments.SingleOrDefault(item => item.CharacterId == role.Id)?.Synthesis ?? new(),
                Rationale = "Verified speech reference and matching attributed voice; generated voices also passed a fresh Chatterbox audition. Generated attributes describe the design request." });
        }
        var replacedIds = replacements.Select(item => item.CharacterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = assignments.Where(item => replacedIds.Contains(item.CharacterId)).ToArray();
        var castingPath = Path.Combine(book.RootPath, "production", "casting.json");
        if (File.Exists(castingPath)) File.Copy(castingPath, Path.Combine(root, $"casting-before-{Guid.NewGuid():N}.json"));
        await store.SaveAsync(book, new CastingPlan { SchemaVersion = 1, BookId = book.Id,
            Assignments = assignments.Where(item => !replacedIds.Contains(item.CharacterId)).Concat(replacements).ToArray(),
            RetiredAssignments = (plan?.RetiredAssignments ?? []).Concat(retired).ToArray(),
            ChapterVoiceUsage = plan?.ChapterVoiceUsage ?? new Dictionary<string, ChapterVoiceUsage>(), VoiceUseCounts = plan?.VoiceUseCounts ?? new Dictionary<string, int>() });
        Console.WriteLine($"Casting updated: {replacements.Count} attributed assignments. Previous casting preserved under {root}.");
    }

    private static async Task<VoiceProfile> GenerateAsync(CharacterProfile role, string root, string library, string forgeRoot, string image, string workerImage, string whisper)
    {
        if (!File.Exists(Path.Combine(forgeRoot, "generate_candidate.py"))) throw new FileNotFoundException($"VoiceForge candidate generator missing at {forgeRoot}. Set --voiceforge to its repository.");
        if (await DockerAsync(["image", "inspect", image], true) != 0 && await DockerAsync(["build", "-f", Path.Combine(forgeRoot, "Dockerfile.design"), "-t", image, forgeRoot]) != 0) throw new IOException("VoiceForge image build failed.");
        var tools = Path.GetFullPath("tools");
        if (!File.Exists(Path.Combine(tools, "verify_generated_voice.py"))) throw new FileNotFoundException($"StoryCast verification tools missing at {tools}. Run from the StoryCast repository.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var resultPath = Path.Combine(root, "candidate-result.json");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            var generated = await DockerAsync(["run", "--rm", "--gpus", "all", "--entrypoint", "python", "--mount", $"type=bind,source={forgeRoot},target=/automation,readonly",
                "--mount", $"type=bind,source={root},target=/requests", "--mount", "type=volume,source=voiceforge-models,target=/models", image,
                "/automation/generate_candidate.py", "/requests/casting-request.json", "--role", role.Id, "--seed", (10000 + attempt * 1009).ToString()]);
            if (generated != 0 || !File.Exists(resultPath)) throw new IOException($"VoiceForge generation failed for '{role.Id}'. See output above; existing casting is preserved.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            var identity = result.RootElement.GetProperty("voiceId").GetString() ?? "";
            if (result.RootElement.GetProperty("roleId").GetString() != role.Id || !Regex.IsMatch(identity, "^voiceforge-[a-f0-9]{32}$")) throw new InvalidDataException("Invalid VoiceForge candidate identity.");
            var candidate = Path.Combine(root, "candidates", identity);
            var verified = await DockerAsync(["run", "--rm", "--gpus", "all", "--entrypoint", "python", "--mount", $"type=bind,source={root},target=/requests",
                "--mount", $"type=bind,source={tools},target=/app/tools,readonly", "--mount", "type=volume,source=storycast-models,target=/models", workerImage,
                "/app/tools/verify_generated_voice.py", $"/requests/candidates/{identity}", "--model", whisper]);
            if (verified != 0) { Console.WriteLine($"VoiceForge: '{role.Id}' candidate {attempt+1}/3 failed verification. Trying another seed. Report: {Path.Combine(candidate, "generated-verification.json")}"); continue; }
            await ImportVerifiedAsync(candidate, library, identity);
            return (await new FileSystemVoiceLibrary().LoadAsync(library)).Single(voice => voice.Id == identity);
        }
        throw new InvalidDataException($"No verified voice for '{role.Id}' after three candidates. Existing casting retained. Candidates: {Path.Combine(root, "candidates")}");
    }

    internal static async Task ImportVerifiedAsync(string candidate, string library, string identity)
    {
        var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate, "generated-verification.json")))!;
        if (report["voiceId"]?.GetValue<string>() != identity || report["status"]?.GetValue<string>() != "pass" || report["reference"]?["status"]?.GetValue<string>() != "pass" || report["audition"]?["status"]?.GetValue<string>() != "pass") throw new InvalidDataException("Voice did not pass reference and audition verification.");
        foreach (var pair in new[] { (File: "sample.wav", Hash: "sampleSha256"), (File: "voice.json", Hash: "manifestSha256") })
            if (report[pair.Hash]?.GetValue<string>() != Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(candidate, pair.File)))).ToLowerInvariant()) throw new InvalidDataException("Verified candidate changed before import.");
        var verificationPath = Path.Combine(library, "voice-verification.json");
        var document = File.Exists(verificationPath) ? JsonNode.Parse(await File.ReadAllTextAsync(verificationPath))! : new JsonObject { ["schemaVersion"] = 1, ["voices"] = new JsonArray() };
        var results = document["voices"]!.AsArray();
        if (results.Any(item => item?["voiceId"]?.GetValue<string>() == identity)) throw new InvalidDataException("Voice already exists in verification report.");
        var staging = Path.Combine(Path.GetDirectoryName(library)!, $".voice-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        foreach (var file in new[] { "sample.wav", "voice.json", "generated-verification.json", "audition.wav" }) File.Copy(Path.Combine(candidate, file), Path.Combine(staging, file));
        results.Add(new JsonObject { ["voiceId"] = identity, ["status"] = "pass", ["generatedVerification"] = report.DeepClone() });
        document["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        document["summary"] = new JsonObject { ["total"] = results.Count, ["passed"] = results.Count(item => item?["status"]?.GetValue<string>() == "pass"), ["review"] = results.Count(item => item?["status"]?.GetValue<string>() == "review"), ["failed"] = results.Count(item => item?["status"]?.GetValue<string>() == "fail") };
        Directory.Move(staging, Path.Combine(library, identity));
        await WriteJsonAsync(verificationPath, document);
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }

    private static async Task<int> DockerAsync(IReadOnlyList<string> arguments, bool quiet = false)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = quiet, RedirectStandardError = quiet };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Docker.");
        var output = quiet ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var error = quiet ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        await Task.WhenAll(process.WaitForExitAsync(), output, error);
        return process.ExitCode;
    }
}
