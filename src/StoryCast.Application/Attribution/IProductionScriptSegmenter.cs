using StoryCast.Domain.Manuscripts;
using StoryCast.Domain.Production;

namespace StoryCast.Application.Attribution;

/// <summary>
/// Divides prepared chapter text into exact narration and dialogue segments.
/// </summary>
public interface IProductionScriptSegmenter
{
    /// <summary>
    /// Creates an unattributed production script without changing source text.
    /// </summary>
    /// <param name="chapter">The prepared source chapter.</param>
    /// <param name="narratorId">The stable narrator identifier.</param>
    /// <returns>
    /// A production script whose dialogue uses the reserved
    /// <c>unassigned</c> speaker identifier.
    /// </returns>
    ChapterProductionScript Segment(
        PreparedChapter chapter,
        string narratorId = "narrator");
}
