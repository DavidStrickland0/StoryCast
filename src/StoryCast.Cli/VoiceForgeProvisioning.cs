using System.Diagnostics;
using System.Text.Json;
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

    internal static async Task EnsureAsync(BookProject book, string library, IVerifiedVoiceLibrary verifiedLibrary,
        IReadOnlySet<string>? requiredSpeakers, string? chapterId, string script, string image, string workerImage, string whisper,
        string ollamaUrl, string model)
    {
        var store = new FileSystemCastingPlanStore();
        var plan = await store.LoadAsync(book);
        var registry = await new FileSystemCharacterRegistryStore().LoadAsync(book)
            ?? throw new InvalidDataException("Discover characters before requesting missing voices.");
        var assignments = plan?.Assignments ?? [];
        var assignedRoles = assignments.Select(assignment => assignment.CharacterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedVoices = assignments.Concat(plan?.RetiredAssignments ?? []).Select(assignment => assignment.VoiceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = registry.Characters.Where(character => !assignedRoles.Contains(character.Id) &&
            (requiredSpeakers is null || requiredSpeakers.Contains(character.Id))).ToList();
        if (!assignedRoles.Contains("narrator")) roles.Add(new CharacterProfile { Id = "narrator", DisplayName = "Narrator", Description = "Clear, steady voice for sustained book narration.", VoiceTraits = ["clear", "steady"], IsNarrator = true });
        if (roles.Count == 0) return;
        Directory.CreateDirectory(library);
        IReadOnlyList<VoiceProfile> voices;
        var raw = await new FileSystemVoiceLibrary().LoadAsync(library);
        if (raw.Count == 0) voices = [];
        else
        {
            try { voices = await verifiedLibrary.LoadAsync(library); }
            catch (InvalidDataException error) when (error.Message == "No voices passed speech verification.") { voices = []; }
        }
        if (!File.Exists(script)) throw new FileNotFoundException("VoiceForge provisioner is missing. Use --voiceforge to select its directory.", script);
        var root = Path.Combine(book.RootPath, "production", "voiceforge");
        Directory.CreateDirectory(root);
        var requestPath = Path.Combine(root, "casting-request.json");
        var temporary = requestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            bookId = book.Id, chapterId,
            roles = roles.Select(role => new { roleId = role.Id, displayName = role.DisplayName,
                description = string.IsNullOrWhiteSpace(role.Description) ? $"A clear, natural voice for {role.DisplayName}." : role.Description,
                presentation = string.IsNullOrWhiteSpace(role.VoicePresentation) ? "unspecified" : role.VoicePresentation.ToLowerInvariant(),
                qualities = role.VoiceTraits, accent = "", age = "" }),
            availableVoices = voices.Where(voice => !usedVoices.Contains(voice.Id)).Select(voice => new { voice.Id, voice.Presentation, voice.Qualities, voice.Accent, voice.ApparentAge, voice.SuitableRoles })
        }, Json));
        File.Move(temporary, requestPath, true);
        var resultPath = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(resultPath, "{\"assignments\":[],\"failedRoles\":[]}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STORYCAST_PYTHON") ?? "python") { UseShellExecute = false };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        foreach (var value in new[] { "-u", script, "--request", requestPath, "--library", Path.GetFullPath(library), "--tools", Path.GetFullPath("tools"), "--image", image, "--worker-image", workerImage, "--whisper-model", whisper, "--ollama-url", ollamaUrl, "--ollama-model", model }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Could not start VoiceForge.");
        await process.WaitForExitAsync();
        var result = JsonSerializer.Deserialize<Result>(await File.ReadAllTextAsync(resultPath), Json)
            ?? throw new InvalidDataException("VoiceForge returned an empty result.");
        if (result.Assignments.Count > 0)
        {
            var eligible = (await verifiedLibrary.LoadAsync(library)).ToDictionary(voice => voice.Id, StringComparer.OrdinalIgnoreCase);
            var additions = new List<CastingAssignment>();
            foreach (var item in result.Assignments)
            {
                var role = roles.SingleOrDefault(role => role.Id == item.RoleId)
                    ?? throw new InvalidDataException("VoiceForge returned an unrequested role.");
                if (!eligible.TryGetValue(item.VoiceId, out var voice) || usedVoices.Contains(item.VoiceId) ||
                    role.VoicePresentation is "male" or "female" && !string.Equals(role.VoicePresentation, voice.Presentation, StringComparison.OrdinalIgnoreCase) ||
                    role.Id != "narrator" && !AttributesMatch(role, voice))
                    throw new InvalidDataException("VoiceForge returned an unverified, reserved, or incompatible voice.");
                usedVoices.Add(item.VoiceId);
                additions.Add(new CastingAssignment { CharacterId = item.RoleId, VoiceId = item.VoiceId, Confidence = 0.8m,
                    Rationale = "Verified unused voice matched the requested character attributes, or a new VoiceForge voice was designed for the role and passed reference and audition verification." });
            }
            await store.SaveAsync(book, new CastingPlan { SchemaVersion = 1, BookId = book.Id,
                Assignments = assignments.Concat(additions).ToArray(), RetiredAssignments = plan?.RetiredAssignments ?? [],
                ChapterVoiceUsage = plan?.ChapterVoiceUsage ?? new Dictionary<string, ChapterVoiceUsage>(), VoiceUseCounts = plan?.VoiceUseCounts ?? new Dictionary<string, int>() });
        }
        if (process.ExitCode != 0 || result.FailedRoles.Count > 0)
            throw new InvalidDataException($"Missing voice requests need review. Queue: {Path.Combine(root, "requests.json")}. Roles: {string.Join(", ", result.FailedRoles)}");
    }

    private sealed class Result
    {
        public List<Assignment> Assignments { get; init; } = [];
        public List<string> FailedRoles { get; init; } = [];
    }

    private static bool AttributesMatch(CharacterProfile role, VoiceProfile voice)
    {
        static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var traits = role.VoiceTraits.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalize).ToArray();
        return traits.Length == 0
            ? voice.SuitableRoles.Contains(role.Id, StringComparer.OrdinalIgnoreCase)
            : traits.All(voice.Qualities.Select(Normalize).ToHashSet().Contains);
    }
    private sealed class Assignment
    {
        public required string RoleId { get; init; }
        public required string VoiceId { get; init; }
    }
}
