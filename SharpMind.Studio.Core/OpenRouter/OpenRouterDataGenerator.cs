using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.DataFactory;

namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>Data Factory generator backed by OpenRouter chat completions.</summary>
public sealed class OpenRouterDataGenerator : IDataFactoryGenerator
{
    private readonly OpenRouterClient _client;
    private readonly OpenRouterConnection _connection;

    public OpenRouterDataGenerator(OpenRouterClient client, OpenRouterConnection connection)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public string Description => $"OpenRouter · {_connection.BaseUrl}";

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        DataFactoryRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        if (!_connection.IsUsable)
            throw new InvalidOperationException("OpenRouter is not configured. Add an API key in Settings.");
        if (string.IsNullOrWhiteSpace(recipe.RemoteModel))
            throw new InvalidOperationException("Choose an OpenRouter model for this recipe.");

        var messages = new List<JsonObject>
        {
            OpenRouterMessageBuilder.System(systemPrompt),
            OpenRouterMessageBuilder.User(userPrompt),
        };

        var text = new StringBuilder();
        await foreach (OpenRouterStreamEvent entry in _client
            .StreamAsync(
                new OpenRouterRequest
                {
                    Model = recipe.RemoteModel!,
                    Messages = messages,
                    Temperature = recipe.Temperature,
                    MaxTokens = recipe.MaxTokens,
                    Seed = recipe.Seed,
                },
                _connection,
                cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(entry.Error))
                throw new OpenRouterException(entry.Error);
            if (!string.IsNullOrEmpty(entry.Delta))
                text.Append(entry.Delta);
        }

        return text.ToString().Trim();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
