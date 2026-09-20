using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoryCast.Application.TextGeneration;

namespace StoryCast.Ollama;

/// <summary>
/// Generates schema-constrained JSON through the Ollama generate API.
/// </summary>
public sealed class OllamaStructuredTextGenerator
    : IStructuredTextGenerator
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private readonly HttpClient httpClient;
    private readonly string model;
    private readonly int? contextLength;
    private readonly int? maximumOutputTokens;

    /// <summary>
    /// Initializes an Ollama structured-output generator.
    /// </summary>
    /// <param name="httpClient">
    /// An HTTP client whose base address points to the Ollama server.
    /// </param>
    /// <param name="model">The installed Ollama model name.</param>
    /// <param name="contextLength">Optional context-window token limit.</param>
    /// <param name="maximumOutputTokens">Optional output token limit.</param>
    public OllamaStructuredTextGenerator(
        HttpClient httpClient,
        string model,
        int? contextLength = null,
        int? maximumOutputTokens = null)
    {
        this.httpClient =
            httpClient ??
            throw new ArgumentNullException(nameof(httpClient));

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.model = model;
        this.contextLength = contextLength;
        this.maximumOutputTokens = maximumOutputTokens;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        JsonElement jsonSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        var request = new GenerateRequest
        {
            Model = model,
            System = systemPrompt,
            Prompt = userPrompt,
            Format = jsonSchema.Clone(),
            Stream = false,
            Think = false,
            Options = new GenerationOptions
            {
                Temperature = 0,
                ContextLength = contextLength,
                MaximumOutputTokens = maximumOutputTokens
            }
        };

        using var response = await httpClient.PostAsJsonAsync(
            "api/generate",
            request,
            SerializerOptions,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ollama returned HTTP {(int)response.StatusCode}: " +
                responseBody,
                inner: null,
                response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<GenerateResponse>(
            responseBody,
            SerializerOptions);

        if (result is null ||
            string.IsNullOrWhiteSpace(result.Response))
        {
            throw new InvalidDataException(
                "Ollama returned an empty structured response.");
        }

        return result.Response;
    }

    private sealed class GenerateRequest
    {
        public string Model { get; init; } = string.Empty;

        public string System { get; init; } = string.Empty;

        public string Prompt { get; init; } = string.Empty;

        public JsonElement Format { get; init; }

        public bool Stream { get; init; }

        public bool Think { get; init; }

        public GenerationOptions Options { get; init; } = new();
    }

    private sealed class GenerationOptions
    {
        public double Temperature { get; init; }

        [JsonPropertyName("num_ctx")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ContextLength { get; init; }

        [JsonPropertyName("num_predict")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaximumOutputTokens { get; init; }
    }

    private sealed class GenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; init; } = string.Empty;
    }
}
