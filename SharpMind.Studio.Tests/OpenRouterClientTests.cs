using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterClientTests
{
    private static readonly OpenRouterConnection Connection =
        new("test-key", "https://openrouter.ai/api/v1");

    [Fact]
    public async Task Stream_ParsesContentToolCallsUsageAndDone()
    {
        const string sse = """
            : OPENROUTER PROCESSING
            data: {"choices":[{"delta":{"content":"Hel"}}]}
            data: {"choices":[{"delta":{"content":"lo"}}]}
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.txt\"}"}}]}}]}
            data: {"choices":[{"delta":{"content":""},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"cost":0.001}}
            data: [DONE]

            """;

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        var events = new List<OpenRouterStreamEvent>();
        await foreach (OpenRouterStreamEvent entry in client.StreamAsync(
            new OpenRouterRequest
            {
                Model = "openai/gpt-4o-mini",
                Messages = [OpenRouterMessageBuilder.User("hi")],
            },
            Connection))
        {
            events.Add(entry);
        }

        Assert.Equal("Hello", string.Concat(events.Select(entry => entry.Delta)));
        var calls = events.SelectMany(entry => entry.ToolCalls ?? []).ToList();
        Assert.Equal("call_1", calls[0].Id);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("{\"path\":\"a.txt\"}", string.Concat(calls.Select(call => call.Arguments)));
        Assert.Equal(10, events.Last(entry => entry.PromptTokens is not null).PromptTokens);
        Assert.Equal(3, events.Last(entry => entry.CompletionTokens is not null).CompletionTokens);
        Assert.Contains(events, entry => entry.IsComplete);
    }

    [Fact]
    public async Task Stream_SurfacesHttpErrors()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"code":401,"message":"No auth credentials found"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        OpenRouterException error = await Assert.ThrowsAsync<OpenRouterException>(async () =>
        {
            await foreach (OpenRouterStreamEvent _ in client.StreamAsync(
                new OpenRouterRequest
                {
                    Model = "openai/gpt-4o-mini",
                    Messages = [OpenRouterMessageBuilder.User("hi")],
                },
                Connection))
            {
            }
        });

        Assert.Equal("No auth credentials found", error.Message);
        Assert.Equal(401, error.StatusCode);
        Assert.Equal(401, error.ErrorCode);
    }

    [Fact]
    public async Task ListModels_ParsesCatalogEntries()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"openai/gpt-4o-mini","name":"GPT-4o mini","context_length":128000}]}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        IReadOnlyList<OpenRouterModel> models = await client.ListModelsAsync(Connection);

        OpenRouterModel model = Assert.Single(models);
        Assert.Equal("openai/gpt-4o-mini", model.Id);
        Assert.Equal("GPT-4o mini", model.Name);
        Assert.Equal(128000, model.ContextLength);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
