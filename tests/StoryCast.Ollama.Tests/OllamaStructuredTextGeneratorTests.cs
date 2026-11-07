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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateAsync_RecoversRepetitionWithChangedSamplingWithinBudget(bool recovers)
    {
        var requests = new List<JsonElement>();
        using var client = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
            return new HttpResponseMessage(recovers && requests.Count == 2 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(recovers && requests.Count == 2
                    ? "{\"response\":\"{\\\"characters\\\":[]}\"}"
                    : "{\"error\":\"prediction aborted, token repeat limit reached\"}")
            };
        })) { BaseAddress = new Uri("http://localhost:11434") };
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var generator = new OllamaStructuredTextGenerator(client, "test");
        if (recovers) Assert.Equal("{\"characters\":[]}", await generator.GenerateAsync("System", "Source", schema.RootElement));
        else await Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync("System", "Source", schema.RootElement));
        Assert.Equal(2, requests.Count);
        Assert.Equal(0, requests[0].GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(0.3, requests[1].GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(requests[0].GetProperty("prompt").GetString(), requests[1].GetProperty("prompt").GetString());
        Assert.Equal(requests[0].GetProperty("format").GetRawText(), requests[1].GetProperty("format").GetRawText());
    }

    [Fact]
    public async Task GenerateAsync_NormalizesTimeoutButPreservesCallerCancellation()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated transport timeout"))))
            { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(7) };
        var generator = new OllamaStructuredTextGenerator(client, "test");
        using var schema = JsonDocument.Parse("{}");
        var timeout = await Assert.ThrowsAsync<HttpRequestException>(() =>
            generator.GenerateAsync("System", "Prompt", schema.RootElement));
        Assert.Contains("timed out after 7 seconds", timeout.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync("System", "Prompt", schema.RootElement, canceled.Token));
    }

    [Fact]
    public async Task GenerateAsync_NormalizesMalformedEnvelope()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") })))
            { BaseAddress = new Uri("http://localhost:11434") };
        using var schema = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => new OllamaStructuredTextGenerator(client, "test")
            .GenerateAsync("System", "Prompt", schema.RootElement));
    }

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
            "test-model",
            contextLength: 32768,
            maximumOutputTokens: 4096);

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
        using var requestDocument = JsonDocument.Parse(capturedRequest);
        var options = requestDocument.RootElement.GetProperty("options");
        Assert.Equal(32768, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(4096, options.GetProperty("num_predict").GetInt32());
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
