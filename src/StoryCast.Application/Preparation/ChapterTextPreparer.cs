using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Preparation;

/// <summary>
/// Prepares plain-text and Markdown chapters for speech production.
/// </summary>
public sealed partial class ChapterTextPreparer
    : IChapterTextPreparer
{
    private const string CurrentPreparationVersion = "3";

    /// <inheritdoc />
    public PreparedChapter Prepare(ManuscriptChapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var normalizedText = chapter.RawText
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var spokenText = chapter.Format switch
        {
            ManuscriptFormat.Markdown =>
                PrepareMarkdown(normalizedText),

            ManuscriptFormat.PlainText =>
                NormalizeWhitespace(normalizedText),

            _ => throw new InvalidDataException(
                $"Unsupported manuscript format: {chapter.Format}")
        };

        if (string.IsNullOrWhiteSpace(spokenText))
        {
            throw new InvalidDataException(
                $"Chapter contains no spoken text: {chapter.SourcePath}");
        }

        var sourceBytes = Encoding.UTF8.GetBytes(
            chapter.RawText);

        var sourceHash = Convert.ToHexString(
            SHA256.HashData(sourceBytes))
            .ToLowerInvariant();

        return new PreparedChapter
        {
            ChapterId = chapter.Id,
            SourcePath = chapter.SourcePath,
            SourceSha256 = sourceHash,
            PreparationVersion = CurrentPreparationVersion,
            SpokenText = spokenText
        };
    }

    private static string PrepareMarkdown(string text)
    {
        text = GenerationInformationFooterRegex().Replace(
            text,
            string.Empty);
text = FenceMarkerRegex().Replace(text, string.Empty);

        text = HorizontalRuleRegex().Replace(
            text,
            "\n\n");

        text = HeadingMarkerRegex().Replace(
            text,
            string.Empty);

        text = BlockQuoteMarkerRegex().Replace(
            text,
            string.Empty);

        text = UnorderedListMarkerRegex().Replace(
            text,
            string.Empty);

        text = OrderedListMarkerRegex().Replace(
            text,
            string.Empty);

        text = ImageRegex().Replace(
            text,
            match => match.Groups["text"].Value);

        text = LinkRegex().Replace(
            text,
            match => match.Groups["text"].Value);

        text = HtmlBreakRegex().Replace(
            text,
            "\n");

        text = HtmlTagRegex().Replace(
            text,
            string.Empty);

        text = InlineCodeMarkerRegex().Replace(
            text,
            string.Empty);

        text = StrongMarkerRegex().Replace(
            text,
            string.Empty);

        text = EmphasisMarkerRegex().Replace(
            text,
            string.Empty);

        text = WebUtility.HtmlDecode(text);

        return NormalizeWhitespace(text);
    }

    private static string NormalizeWhitespace(string text)
    {
        var lines = text
            .Split('\n')
            .Select(line => TrailingWhitespaceRegex()
                .Replace(line, string.Empty))
            .ToArray();

        text = string.Join('\n', lines);
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    [GeneratedRegex(
        @"(?m)^[ \t]*(```|~~~)[^\n]*\n?",
        RegexOptions.CultureInvariant)]
    private static partial Regex FenceMarkerRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]{0,3}((\*[ \t]*){3,}|(-[ \t]*){3,}|(_[ \t]*){3,})[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]{0,3}#{1,6}[ \t]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarkerRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]{0,3}>[ \t]?",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlockQuoteMarkerRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]*[-+*][ \t]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnorderedListMarkerRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]*\d+[.)][ \t]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListMarkerRegex();

    [GeneratedRegex(
        @"!\[(?<text>[^\]]*)\]\([^)]+\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(
        @"\[(?<text>[^\]]+)\]\([^)]+\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(
        @"<br\s*/?>",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex HtmlBreakRegex();

    [GeneratedRegex(
        @"<[^>]+>",
        RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(
        @"`+",
        RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeMarkerRegex();

    [GeneratedRegex(
        @"(\*\*|__)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StrongMarkerRegex();

    [GeneratedRegex(
        @"(?<!\w)[*_]+|[*_]+(?!\w)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmphasisMarkerRegex();

[GeneratedRegex(
        @"(?ms)(?:^|\n)[ \t]*Generation Information[ \t]*\n.*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex GenerationInformationFooterRegex();

    [GeneratedRegex(
        @"[ \t]+$",
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(
        @"\n[ \t]*\n(?:[ \t]*\n)+",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLinesRegex();
}
