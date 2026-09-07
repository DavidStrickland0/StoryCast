using StoryCast.Domain.Characters;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Validates and applies generated dialogue-speaker assignments.
/// </summary>
public sealed class DialogueAttributionApplicator
{
    /// <summary>
    /// Applies exactly one character assignment to every dialogue segment.
    /// </summary>
    /// <param name="script">The unattributed production script.</param>
    /// <param name="registry">The established character registry.</param>
    /// <param name="assignments">The generated dialogue assignments.</param>
    /// <returns>The fully attributed production script.</returns>
    public ChapterProductionScript Apply(
        ChapterProductionScript script,
        CharacterRegistry registry,
        IReadOnlyList<DialogueAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assignments);

        var charactersById = registry.Characters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);

        var assignmentsByIndex =
            new Dictionary<int, DialogueAssignment>();

        foreach (var assignment in assignments)
        {
            if (!assignmentsByIndex.TryAdd(
                    assignment.SegmentIndex,
                    assignment))
            {
                throw new InvalidDataException(
                    $"Dialogue segment {assignment.SegmentIndex} was " +
                    "assigned more than once.");
            }

            if (assignment.Confidence < 0 ||
                assignment.Confidence > 1)
            {
                throw new InvalidDataException(
                    $"Dialogue segment {assignment.SegmentIndex} has " +
                    $"invalid confidence {assignment.Confidence}.");
            }

            if (!charactersById.ContainsKey(
                    assignment.SpeakerId))
            {
                throw new InvalidDataException(
                    $"Dialogue segment {assignment.SegmentIndex} uses " +
                    $"unknown character ID '{assignment.SpeakerId}'.");
            }
        }

        var attributedSegments =
            new List<ProductionSegment>(
                script.Segments.Count);

        foreach (var segment in script.Segments)
        {
            var hasAssignment = assignmentsByIndex.TryGetValue(
                segment.Index,
                out var assignment);

            if (segment.Kind == SegmentKind.Narration)
            {
                if (hasAssignment)
                {
                    throw new InvalidDataException(
                        $"Narration segment {segment.Index} cannot receive " +
                        "a dialogue-speaker assignment.");
                }

                attributedSegments.Add(CopyNarration(segment));
                continue;
            }

            if (!hasAssignment || assignment is null)
            {
                throw new InvalidDataException(
                    $"Dialogue segment {segment.Index} was not assigned.");
            }

            attributedSegments.Add(
                CopyDialogue(
                    segment,
                    assignment));
        }

        var dialogueCount = script.Segments.Count(
            segment => segment.Kind == SegmentKind.Dialogue);

        if (assignmentsByIndex.Count != dialogueCount)
        {
            throw new InvalidDataException(
                $"Expected {dialogueCount} dialogue assignments but " +
                $"received {assignmentsByIndex.Count}.");
        }

        var attributedScript = new ChapterProductionScript
        {
            ChapterId = script.ChapterId,
            SourceText = script.SourceText,
            Segments = attributedSegments
        };

        var validationErrors =
            new ProductionScriptValidator().Validate(
                attributedScript);

        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException(
                "Attributed production script failed exact-text " +
                $"validation: {string.Join(" | ", validationErrors)}");
        }

        return attributedScript;
    }

    private static ProductionSegment CopyNarration(
        ProductionSegment source)
    {
        return new ProductionSegment
        {
            Index = source.Index,
            SourceStart = source.SourceStart,
            SourceLength = source.SourceLength,
            SourceText = source.SourceText,
            SpeakerId = source.SpeakerId,
            Kind = source.Kind,
            Delivery = source.Delivery,
            AttributionConfidence = source.AttributionConfidence,
            AttributionRationale = source.AttributionRationale
        };
    }

    private static ProductionSegment CopyDialogue(
        ProductionSegment source,
        DialogueAssignment assignment)
    {
        return new ProductionSegment
        {
            Index = source.Index,
            SourceStart = source.SourceStart,
            SourceLength = source.SourceLength,
            SourceText = source.SourceText,
            SpeakerId = assignment.SpeakerId,
            Kind = source.Kind,
            Delivery = assignment.Delivery.Trim(),
            AttributionConfidence = assignment.Confidence,
            AttributionRationale = assignment.Rationale.Trim()
        };
    }
}
