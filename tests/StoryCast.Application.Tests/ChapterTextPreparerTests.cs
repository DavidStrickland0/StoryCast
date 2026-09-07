using StoryCast.Application.Preparation;
using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Tests.Preparation;

/// <summary>
/// Tests deterministic preparation of manuscript text.
/// </summary>
public sealed class ChapterTextPreparerTests
{
    /// <summary>
    /// Verifies that Markdown formatting is removed without rewriting words.
    /// </summary>
    [Fact]
    public void Prepare_RemovesMarkdownFormatting()
    {
        var chapter = new ManuscriptChapter
        {
            Id = "chapter-001",
            Index = 0,
            FileName = "chapter-001.md",
            SourcePath = @"C:\Book\chapter-001.md",
            Format = ManuscriptFormat.Markdown,
            RawText =
                """
                # Chapter One

                Miller **looked** up.

                > "We need to move," she said.

                ---

                The [door](https://example.com) opened.
                """
        };

        var preparer = new ChapterTextPreparer();

        var prepared = preparer.Prepare(chapter);

        Assert.Equal(
            """
            Chapter One

            Miller looked up.

            "We need to move," she said.

            The door opened.
            """,
            prepared.SpokenText);

        Assert.Equal(64, prepared.SourceSha256.Length);
        Assert.Equal("1", prepared.PreparationVersion);
    }

    /// <summary>
    /// Verifies that internal underscores in plain text are preserved.
    /// </summary>
    [Fact]
    public void Prepare_PreservesPlainTextExactlyExceptLineEndings()
    {
        var chapter = new ManuscriptChapter
        {
            Id = "chapter-001",
            Index = 0,
            FileName = "chapter-001.txt",
            SourcePath = @"C:\Book\chapter-001.txt",
            Format = ManuscriptFormat.PlainText,
            RawText = "System_ID remained active.\r\nNext line."
        };

        var preparer = new ChapterTextPreparer();

        var prepared = preparer.Prepare(chapter);

        Assert.Equal(
            "System_ID remained active.\nNext line.",
            prepared.SpokenText);
    }

    /// <summary>
    /// Verifies that identical input produces identical hashes and spoken text.
    /// </summary>
    [Fact]
    public void Prepare_IsDeterministic()
    {
        var chapter = new ManuscriptChapter
        {
            Id = "chapter-001",
            Index = 0,
            FileName = "chapter-001.md",
            SourcePath = @"C:\Book\chapter-001.md",
            Format = ManuscriptFormat.Markdown,
            RawText = "# Test\n\nIdentical source."
        };

        var preparer = new ChapterTextPreparer();

        var first = preparer.Prepare(chapter);
        var second = preparer.Prepare(chapter);

        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(first.SpokenText, second.SpokenText);
    }
}
