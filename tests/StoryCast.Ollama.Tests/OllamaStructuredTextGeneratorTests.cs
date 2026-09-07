using System.Net;
using System.Text;
using System.Text.Json;
using StoryCast.Ollama;

namespace StoryCast.Ollama.Tests;

/// <summary>
/// Tests Ollama structured-output HTTP requests.
/// </summary>
public sealed class OllamaStructuredTextGeneratorTests
{
    /// <summary>
    /// Verifies the request contract and extracted generated response.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_SendsSchemaAndReturnsResponse()
    {
        string? capturedRequest = null;

        var handler = new StubHttpMessageHandler(
            async request =>
            {
                capturedRequest = await request.Content!.ReadAsStringAsync();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "model": "test-model",
                          "response": "{\"value\":\"accepted\"}",
                          "done": true
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434/")
        };

        var generator = new OllamaStructuredTextGenerator(
            httpClient,
            "test-model");

        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "value": {
                  "type": "string"
                }
              },
              "required": [
                "value"
              ]
            }
            """);

        var result = await generator.GenerateAsync(
            "Return structured data.",
            "Generate the value.",
            schemaDocument.RootElement);

        Assert.Equal(
            "{\"value\":\"accepted\"}",
            result);

        Assert.NotNull(capturedRequest);
        Assert.Contains(
            "\"model\":\"test-model\"",
            capturedRequest,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"format\":{\"type\":\"object\"",
            capturedRequest,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"stream\":false",
            capturedRequest,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"think\":false",
            capturedRequest,
            StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler
        : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            Task<HttpResponseMessage>> handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
