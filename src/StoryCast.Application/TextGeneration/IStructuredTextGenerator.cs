using System.Text.Json;

namespace StoryCast.Application.TextGeneration;

/// <summary>
/// Generates JSON constrained by a supplied JSON schema.
/// </summary>
public interface IStructuredTextGenerator
{
    /// <summary>
    /// Generates one schema-constrained JSON response.
    /// </summary>
    /// <param name="systemPrompt">The behavioral system instruction.</param>
    /// <param name="userPrompt">The task and input data.</param>
    /// <param name="jsonSchema">The required response schema.</param>
    /// <param name="cancellationToken">
    /// A token that may cancel generation.
    /// </param>
    /// <returns>The generated JSON document text.</returns>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        JsonElement jsonSchema,
        CancellationToken cancellationToken = default);
}
