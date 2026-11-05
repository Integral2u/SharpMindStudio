using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMind.Studio.ComputeHost;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Storage;

ComputeOptions options = ComputeOptions.FromEnvironment();
StudioPaths.UseRoot(options.DataRoot);

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<StudioModelHost>();
builder.Services.AddSingleton<ModelRuntime>();
builder.Services.AddSingleton<TrainingJobManager>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (options.AllowAnonymous
        || context.Request.Path.StartsWithSegments("/health")
        || context.Request.Path == "/")
    {
        await next();
        return;
    }

    if (string.IsNullOrWhiteSpace(options.Token))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = "SHARPMIND_TOKEN is not configured. Set it before exposing this host.",
            },
        });
        return;
    }

    string expected = "Bearer " + options.Token;
    string actual = context.Request.Headers.Authorization.ToString();
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { message = "Unauthorized." },
        });
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Json(new
{
    name = "SharpMind Compute",
    status = "ok",
    docs = "/health, /v1/models, /v1/chat/completions, /v1/training/jobs",
}));

app.MapGet("/health", (ModelRuntime runtime, TrainingJobManager jobs) => Results.Json(new
{
    status = "ok",
    model = options.ModelId,
    modelPath = options.ModelPath,
    modelConfigured = !string.IsNullOrWhiteSpace(options.ModelPath)
        && File.Exists(options.ModelPath),
    modelLoaded = runtime.IsLoaded,
    dataRoot = options.DataRoot,
    runningJobs = jobs.List().Count(job => job.Status == "running"),
}));

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[]
    {
        new
        {
            id = options.ModelId,
            @object = "model",
            owned_by = "sharpmind",
        },
    },
}));

app.MapPost("/v1/chat/completions", async (
    ChatCompletionRequest request,
    ModelRuntime runtime,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (request.Messages.Count == 0)
        return Results.BadRequest(new { error = new { message = "messages is required." } });

    if (request.Stream)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (string token in runtime.GenerateAsync(request, cancellationToken))
            {
                string chunk = new JsonObject
                {
                    ["id"] = "chatcmpl-sharpmind",
                    ["object"] = "chat.completion.chunk",
                    ["model"] = runtime.ModelId,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject
                        {
                            ["content"] = token,
                        },
                        ["finish_reason"] = null,
                    }),
                }.ToJsonString();

                await context.Response.WriteAsync($"data: {chunk}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            string done = new JsonObject
            {
                ["id"] = "chatcmpl-sharpmind",
                ["object"] = "chat.completion.chunk",
                ["model"] = runtime.ModelId,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop",
                }),
            }.ToJsonString();
            await context.Response.WriteAsync($"data: {done}\n\n", cancellationToken);
            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller disconnected.
        }
        catch (Exception ex)
        {
            string error = new JsonObject { ["error"] = new JsonObject { ["message"] = ex.Message } }
                .ToJsonString();
            await context.Response.WriteAsync($"data: {error}\n\n", CancellationToken.None);
            await context.Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
            await context.Response.Body.FlushAsync(CancellationToken.None);
        }

        return Results.Empty;
    }

    try
    {
        var text = new StringBuilder();
        await foreach (string token in runtime.GenerateAsync(request, cancellationToken))
            text.Append(token);

        return Results.Json(new
        {
            id = "chatcmpl-sharpmind",
            @object = "chat.completion",
            model = runtime.ModelId,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = text.ToString(),
                    },
                    finish_reason = "stop",
                },
            },
        });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = new { message = ex.Message } },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/v1/training/jobs", (
    TrainingJobRequest request,
    TrainingJobManager jobs) =>
{
    if (request.Job is null)
        return Results.BadRequest(new { error = new { message = "job is required." } });

    TrainingJobState state = jobs.Start(request, options);
    return Results.Json(new { id = state.Id, status = state.Status }, statusCode: 202);
});

app.MapGet("/v1/training/jobs", (TrainingJobManager jobs) => Results.Json(
    jobs.List().Select(job => new
    {
        job.Id,
        job.Status,
        job.Progress,
        job.Step,
        job.Loss,
        job.ValLoss,
        job.Success,
        job.ExportPath,
        job.Error,
        job.CreatedUtc,
        job.FinishedUtc,
    })));

app.MapGet("/v1/training/jobs/{id}", (string id, TrainingJobManager jobs) =>
    jobs.Get(id) is { } state ? Results.Json(state) : Results.NotFound());

app.MapPost("/v1/training/jobs/{id}/cancel", (string id, TrainingJobManager jobs) =>
    jobs.Cancel(id) ? Results.Ok(new { id, status = "cancelling" }) : Results.NotFound());

app.MapGet("/v1/training/jobs/{id}/logs", (string id, TrainingJobManager jobs) =>
    jobs.Get(id) is { } state
        ? Results.Json(new { state.Id, state.Status, state.Progress, state.Step, state.Log })
        : Results.NotFound());

app.MapGet("/v1/training/jobs/{id}/artifacts", (string id, TrainingJobManager jobs) =>
    jobs.Get(id) is { } state
        ? Results.Json(new
        {
            state.Id,
            state.Status,
            state.ExportPath,
            state.AdapterPath,
        })
        : Results.NotFound());

app.Run();

public partial class Program;
