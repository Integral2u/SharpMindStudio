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
    public async Task Stream_SendsAppAttributionHeaders()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        await foreach (OpenRouterStreamEvent _ in client.StreamAsync(
            new OpenRouterRequest
            {
                Model = "openai/gpt-4o-mini",
                Messages = [OpenRouterMessageBuilder.User("hi")],
            },
            Connection))
        {
        }

        Assert.NotNull(captured);
        Assert.Equal(
            "https://integral2u.github.io/SharpMindStudio/",
            captured!.Headers.GetValues("HTTP-Referer").Single());
        Assert.Equal(
            "SharpMind Studio",
            captured.Headers.GetValues("X-OpenRouter-Title").Single());
    }

    [Fact]
    public async Task ListModels_ParsesCatalogEntries()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"data":[
                  {"id":"openai/gpt-4o-mini","name":"GPT-4o mini","context_length":128000,
                   "pricing":{"prompt":"0.00000015","completion":"0.0000006"},
                   "supported_parameters":["tools","temperature"]},
                  {"id":"meta/llama-3.3-70b:free","name":"Llama Free","context_length":131072,
                   "pricing":{"prompt":"0","completion":"0"},
                   "supported_parameters":["tools"]}
                ]}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        IReadOnlyList<OpenRouterModel> models = await client.ListModelsAsync(Connection);

        Assert.Equal(2, models.Count);
        OpenRouterModel free = models[0];
        Assert.Equal("meta/llama-3.3-70b:free", free.Id);
        Assert.True(free.IsFree);
        Assert.True(free.SupportsTools);
        Assert.Equal(131072, free.ContextLength);
        Assert.False(models[1].IsFree);
        Assert.True(models[1].SupportsTools);
    }

    [Fact]
    public async Task Stream_DoesNotSendAttributionForEuroRouter()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        var euro = new OpenRouterConnection(
            "eur_secret", "https://api.eurouter.ai/api/v1", Referer: null, Title: null);
        await foreach (OpenRouterStreamEvent _ in client.StreamAsync(
            new OpenRouterRequest
            {
                Model = "openai/gpt-4.1",
                Messages = [OpenRouterMessageBuilder.User("hi")],
            },
            euro))
        {
        }

        Assert.NotNull(captured);
        Assert.Equal("https://api.eurouter.ai/api/v1/chat/completions",
            captured!.RequestUri!.ToString());
        Assert.False(captured.Headers.Contains("HTTP-Referer"));
        Assert.False(captured.Headers.Contains("X-OpenRouter-Title"));
    }

    [Fact]
    public async Task Stream_ParsesCostCurrency()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"cost":0.0042,"cost_currency":"EUR"}}
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
                Model = "openai/gpt-4.1",
                Messages = [OpenRouterMessageBuilder.User("hi")],
            },
            new OpenRouterConnection("eur_secret", "https://api.eurouter.ai/api/v1")))
        {
            events.Add(entry);
        }

        OpenRouterStreamEvent usage = events.Last(entry => entry.Cost is not null);
        Assert.Equal(0.0042m, usage.Cost);
        Assert.Equal("EUR", usage.CostCurrency);
    }

    [Fact]
    public async Task GetKeyInfo_ParsesEuroRouterUsage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"data":{"name":"SharpMind","label":"Studio key","limit":20,"limit_remaining":12.5,"usage":7.5}}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        RemoteKeyInfo info = await client.GetKeyInfoAsync(
            new OpenRouterConnection("eur_secret", "https://api.eurouter.ai/api/v1"));

        Assert.Equal("Studio key", info.Label);
        Assert.Equal("SharpMind", info.Name);
        Assert.Equal(20m, info.Limit);
        Assert.Equal(12.5m, info.Remaining);
        Assert.Equal(7.5m, info.Usage);
    }

    [Fact]
    public async Task Http402_AddsBalanceHint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(
                """{"error":{"code":402,"message":"Insufficient credits"}}""",
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
                    Model = "openai/gpt-4.1",
                    Messages = [OpenRouterMessageBuilder.User("hi")],
                },
                new OpenRouterConnection("eur_secret", "https://api.eurouter.ai/api/v1")))
            {
            }
        });

        Assert.Equal(402, error.StatusCode);
        Assert.Contains("balance is too low", error.Message);
        Assert.Contains("Insufficient credits", error.Message);
    }

    [Fact]
    public async Task ListModels_WorksWithoutAKey()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"openai/gpt-4.1","name":"GPT-4.1"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var client = new OpenRouterClient(http);

        IReadOnlyList<OpenRouterModel> models = await client.ListModelsAsync(
            new OpenRouterConnection("", "https://api.eurouter.ai/api/v1", null, null));

        Assert.Single(models);
        Assert.Equal("openai/gpt-4.1", models[0].Id);
        Assert.NotNull(captured);
        Assert.Null(captured!.Headers.Authorization);
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
