using StoryCast.Domain.Manuscripts;

namespace StoryCast.Application.Preparation;

/// <summary>
/// Converts manuscript chapters into deterministic spoken text.
/// </summary>
public interface IChapterTextPreparer
{
    /// <summary>
    /// Prepares one chapter for speaker attribution and speech synthesis.
    /// </summary>
    /// <param name="chapter">The source chapter.</param>
    /// <returns>The deterministically prepared chapter.</returns>
    PreparedChapter Prepare(ManuscriptChapter chapter);
}
