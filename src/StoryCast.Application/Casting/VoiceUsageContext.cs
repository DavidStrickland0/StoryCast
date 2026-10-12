using StoryCast.Application.Preparation;
using StoryCast.Application.Production;
using StoryCast.Domain.Books;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>Verified attribution history for per-voice speech counts.</summary>
public sealed class VoiceUsageContext
{
    /// <summary>Current zero-based manuscript position.</summary>
    public required int ChapterIndex { get; init; }
    /// <summary>Current stable chapter identifier.</summary>
    public required string ChapterId { get; init; }
    /// <summary>Current source hash.</summary>
    public required string SourceSha256 { get; init; }
    /// <summary>Current attributed speech counts by speaker.</summary>
    public required IReadOnlyDictionary<string, int> CurrentRoleUseCounts { get; init; }
    /// <summary>Recorded voice usage by chapter.</summary>
    public required IReadOnlyDictionary<string, ChapterVoiceUsage> History { get; init; }

    /// <summary>Loads source-matched attribution, or omits counts when history is incomplete.</summary>
    public static async Task<VoiceUsageContext?> LoadAsync(
        BookProject book, string chapterId, IProductionScriptStore scripts,
        CastingPlan? plan, CancellationToken cancellationToken = default)
    {
        var target = book.Manuscript.Chapters.SingleOrDefault(chapter => chapter.Id == chapterId)
            ?? throw new ArgumentException($"Unknown casting chapter '{chapterId}'.");
        var history = new Dictionary<string, ChapterVoiceUsage>(plan?.ChapterVoiceUsage ?? new Dictionary<string, ChapterVoiceUsage>(), StringComparer.OrdinalIgnoreCase);
        var assigned = (plan?.Assignments ?? []).ToDictionary(assignment => assignment.CharacterId, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> currentCounts = new Dictionary<string, int>();
        var preparer = new ChapterTextPreparer();
        string sourceHash = "";
        foreach (var chapter in book.Manuscript.Chapters.Where(chapter => chapter.Index <= target.Index))
        {
            var artifact = await scripts.LoadAsync(book, chapter.Id, cancellationToken);
            var prepared = preparer.Prepare(chapter);
            // Missing or stale attribution cannot establish accurate counts.
            if (artifact is null || !string.Equals(artifact.SourceSha256, prepared.SourceSha256, StringComparison.OrdinalIgnoreCase)) return null;
            var counts = artifact.Script.Segments.Where(segment => !string.IsNullOrWhiteSpace(segment.SourceText))
                .GroupBy(segment => segment.SpeakerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            if (chapter.Index == target.Index)
            {
                currentCounts = counts;
                sourceHash = artifact.SourceSha256;
                continue;
            }
            if (history.TryGetValue(chapter.Id, out var recorded))
            {
                if (!string.Equals(recorded.SourceSha256, artifact.SourceSha256, StringComparison.OrdinalIgnoreCase) || recorded.ChapterIndex != chapter.Index) return null;
                continue;
            }
            if ((plan?.RetiredAssignments.Count ?? 0) > 0) return null;
            if (counts.Keys.Any(role => !assigned.ContainsKey(role))) return null;
            history[chapter.Id] = new ChapterVoiceUsage
            {
                ChapterIndex = chapter.Index, SourceSha256 = artifact.SourceSha256,
                VoiceUseCounts = counts.GroupBy(pair => assigned[pair.Key].VoiceId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.OrdinalIgnoreCase)
            };
        }
        return new VoiceUsageContext
        {
            ChapterId = chapterId, ChapterIndex = target.Index, SourceSha256 = sourceHash,
            CurrentRoleUseCounts = currentCounts, History = history
        };
    }
}
