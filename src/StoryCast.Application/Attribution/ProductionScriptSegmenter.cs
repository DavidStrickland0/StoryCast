using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Deterministically divides text around paired quotation marks.
/// </summary>
public sealed class ProductionScriptSegmenter
    : IProductionScriptSegmenter
{
    /// <summary>
    /// Gets the reserved speaker identifier used before dialogue attribution.
    /// </summary>
    public const string UnassignedSpeakerId = "unassigned";

    /// <inheritdoc />
    public ChapterProductionScript Segment(
        PreparedChapter chapter,
        string narratorId = "narrator")
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(narratorId);

        var text = chapter.SpokenText;
        var segments = new List<ProductionSegment>();
        var cursor = 0;

        while (cursor < text.Length)
        {
            var openingQuote = FindNextOpeningQuote(
                text,
                cursor);

            if (openingQuote < 0)
            {
                AddSegment(
                    segments,
                    text,
                    cursor,
                    text.Length - cursor,
                    narratorId,
                    SegmentKind.Narration);

                cursor = text.Length;
                continue;
            }

            if (openingQuote > cursor)
            {
                AddSegment(
                    segments,
                    text,
                    cursor,
                    openingQuote - cursor,
                    narratorId,
                    SegmentKind.Narration);
            }

            var closingQuote = FindClosingQuote(
                text,
                openingQuote);

            if (closingQuote < 0)
            {
                AddSegment(
                    segments,
                    text,
                    openingQuote,
                    text.Length - openingQuote,
                    narratorId,
                    SegmentKind.Narration);

                cursor = text.Length;
                continue;
            }

            AddSegment(
                segments,
                text,
                openingQuote,
                closingQuote - openingQuote + 1,
                UnassignedSpeakerId,
                SegmentKind.Dialogue);

            cursor = closingQuote + 1;
        }

        if (segments.Count == 0 && text.Length == 0)
        {
            throw new InvalidDataException(
                $"Prepared chapter contains no spoken text: " +
                chapter.SourcePath);
        }

        return new ChapterProductionScript
        {
            ChapterId = chapter.ChapterId,
            SourceText = text,
            Segments = segments
        };
    }

    private static int FindNextOpeningQuote(
        string text,
        int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '\u201C')
            {
                return index;
            }

            if (text[index] == '"' &&
                !IsEscaped(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindClosingQuote(
        string text,
        int openingQuote)
    {
        var openingCharacter = text[openingQuote];
        var closingCharacter =
            openingCharacter == '\u201C' ? '\u201D' : '"';

        for (var index = openingQuote + 1;
             index < text.Length;
             index++)
        {
            if (text[index] == closingCharacter &&
                !IsEscaped(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsEscaped(
        string text,
        int position)
    {
        var backslashCount = 0;

        for (var index = position - 1;
             index >= 0 && text[index] == '\\';
             index--)
        {
            backslashCount++;
        }

        return backslashCount % 2 != 0;
    }

    private static void AddSegment(
        List<ProductionSegment> segments,
        string source,
        int start,
        int length,
        string speakerId,
        SegmentKind kind)
    {
        if (length <= 0)
        {
            return;
        }

        var sourceText = source.Substring(
            start,
            length);

        if (string.IsNullOrWhiteSpace(sourceText) &&
            segments.Count > 0)
        {
            var previous = segments[^1];

            segments[^1] = new ProductionSegment
            {
                Index = previous.Index,
                SourceStart = previous.SourceStart,
                SourceLength =
                    previous.SourceLength + length,
                SourceText =
                    previous.SourceText + sourceText,
                SpeakerId = previous.SpeakerId,
                Kind = previous.Kind,
                Delivery = previous.Delivery,
                AttributionConfidence =
                    previous.AttributionConfidence,
                AttributionRationale =
                    previous.AttributionRationale
            };

            return;
        }

        if (segments.Count == 1 &&
            string.IsNullOrWhiteSpace(
                segments[0].SourceText))
        {
            var leadingWhitespace = segments[0];

            segments[0] = new ProductionSegment
            {
                Index = 0,
                SourceStart =
                    leadingWhitespace.SourceStart,
                SourceLength =
                    leadingWhitespace.SourceLength + length,
                SourceText =
                    leadingWhitespace.SourceText + sourceText,
                SpeakerId = speakerId,
                Kind = kind
            };

            return;
        }

        segments.Add(
            new ProductionSegment
            {
                Index = segments.Count,
                SourceStart = start,
                SourceLength = length,
                SourceText = sourceText,
                SpeakerId = speakerId,
                Kind = kind
            });
    }
}
