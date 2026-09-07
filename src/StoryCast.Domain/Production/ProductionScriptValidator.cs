namespace StoryCast.Domain.Production;

/// <summary>
/// Verifies that a production script preserves its complete source text.
/// </summary>
public sealed class ProductionScriptValidator
{
    /// <summary>
    /// Validates segment order, source offsets, and exact text coverage.
    /// </summary>
    /// <param name="script">The production script to validate.</param>
    /// <returns>
    /// An empty collection when the script is valid; otherwise, descriptions
    /// of every detected validation failure.
    /// </returns>
    public IReadOnlyList<string> Validate(
        ChapterProductionScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var errors = new List<string>();
        var expectedStart = 0;

        for (var position = 0;
             position < script.Segments.Count;
             position++)
        {
            var segment = script.Segments[position];

            if (segment.Index != position)
            {
                errors.Add(
                    $"Segment at position {position} has index " +
                    $"{segment.Index}; expected {position}.");
            }

            if (segment.SourceStart != expectedStart)
            {
                errors.Add(
                    $"Segment {segment.Index} begins at source offset " +
                    $"{segment.SourceStart}; expected {expectedStart}.");
            }

            if (segment.SourceLength != segment.SourceText.Length)
            {
                errors.Add(
                    $"Segment {segment.Index} declares source length " +
                    $"{segment.SourceLength}, but contains " +
                    $"{segment.SourceText.Length} characters.");
            }

            if (string.IsNullOrWhiteSpace(segment.SpeakerId))
            {
                errors.Add(
                    $"Segment {segment.Index} does not identify a speaker.");
            }

            var segmentEnd =
                segment.SourceStart + segment.SourceLength;

            if (segment.SourceStart < 0 ||
                segment.SourceLength < 0 ||
                segmentEnd > script.SourceText.Length)
            {
                errors.Add(
                    $"Segment {segment.Index} falls outside the chapter " +
                    "source text.");
            }
            else
            {
                var sourceSlice = script.SourceText.Substring(
                    segment.SourceStart,
                    segment.SourceLength);

                if (!string.Equals(
                        sourceSlice,
                        segment.SourceText,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Segment {segment.Index} does not exactly match " +
                        "its source-text range.");
                }
            }

            expectedStart = segmentEnd;
        }

        if (expectedStart != script.SourceText.Length)
        {
            errors.Add(
                $"Segments cover {expectedStart} of " +
                $"{script.SourceText.Length} source characters.");
        }

        return errors;
    }
}
