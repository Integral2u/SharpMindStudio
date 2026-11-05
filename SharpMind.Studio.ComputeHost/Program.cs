using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMind.Studio.ComputeHost;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Storage;

ComputeOptions options = ComputeOptions.FromEnvironment();
StudioPaths.UseRoot(options.DataRoot);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = 8L * 1024 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ComputeAccelerators>();
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
    docs = "/health, /v1/models, /v1/files, /v1/chat/completions, /v1/training/jobs",
}));

app.MapGet("/health", (
    ModelRuntime runtime,
    TrainingJobManager jobs,
    ComputeAccelerators accelerators) => Results.Json(new
{
    status = "ok",
    model = runtime.ModelId,
    modelPath = runtime.ModelPath,
    modelConfigured = options.ModelConfigured,
    modelLoaded = runtime.IsLoaded,
    dataRoot = options.DataRoot,
    modelsFolder = options.ModelsFolder,
    filesFolder = options.FilesFolder,
    runningJobs = jobs.List().Count(job => job.Status == "running"),
    accelerators = accelerators.Plugins.Select(plugin => new
    {
        name = plugin.Name,
        description = plugin.Description,
        inference = plugin.Capabilities.Any(
            capability => capability is SharpMind.Inference.IInferenceEngineFactory),
        backend = plugin.Capabilities
            .OfType<SharpMind.Core.Plugins.IBackendHintProvider>()
            .Select(hint => hint.BackendHint())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
    }),
    acceleratorWarnings = accelerators.Warnings,
    acceleratorReason = runtime.AcceleratorReason,
}));

app.MapGet("/v1/models", (ModelRuntime runtime) => Results.Json(new
{
    @object = "list",
    data = ListedModels(runtime, options),
}));

app.MapPut("/v1/models/{name}", async (
    string name,
    HttpContext context,
    ModelRuntime runtime,
    CancellationToken cancellationToken) =>
{
    string fileName = Path.GetFileName(name);
    if (!options.IsSupportedModelName(fileName))
        return Results.BadRequest(new { error = new { message = "Model name must end in .smm or .gguf." } });

    Directory.CreateDirectory(options.ModelsFolder);
    string destination = Path.Combine(options.ModelsFolder, fileName);

    long bytes;
    try
    {
        bytes = await HostFiles.SaveAsync(context.Request.Body, destination, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        return Results.Empty;
    }

    if (bytes == 0)
    {
        try { File.Delete(destination); } catch { }
        return Results.BadRequest(new { error = new { message = "The upload was empty." } });
    }

    bool activate = string.Equals(
        context.Request.Query["activate"], "true", StringComparison.OrdinalIgnoreCase);
    if (activate)
    {
        try
        {
            await runtime.ActivateAsync(destination, cancellationToken);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    error = new { message = $"Uploaded, but the model did not load: {ex.Message}" },
                    path = destination,
                    bytes,
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    return Results.Json(new
    {
        id = Path.GetFileNameWithoutExtension(destination),
        name = fileName,
        path = destination,
        bytes,
        active = string.Equals(runtime.ModelPath, destination, StringComparison.OrdinalIgnoreCase),
        loaded = runtime.IsLoaded,
    });
});

app.MapPost("/v1/models/activate", async (
    ActivateModelRequest request,
    ModelRuntime runtime,
    CancellationToken cancellationToken) =>
{
    string? path = options.ResolveModelFile(request.Name ?? "");
    if (path is null)
        return Results.NotFound(new { error = new { message = "No such model in the models folder." } });

    try
    {
        await runtime.ActivateAsync(path, cancellationToken);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = new { message = ex.Message } },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    return Results.Json(new { id = runtime.ModelId, path, loaded = runtime.IsLoaded });
});

app.MapPost("/v1/models/unload", async (ModelRuntime runtime, CancellationToken cancellationToken) =>
{
    await runtime.ActivateAsync(null, cancellationToken);
    return Results.Json(new { id = runtime.ModelId, loaded = runtime.IsLoaded });
});

app.MapDelete("/v1/models/{name}", async (
    string name,
    ModelRuntime runtime,
    CancellationToken cancellationToken) =>
{
    string? path = options.ResolveModelFile(name);
    if (path is null) return Results.NotFound();

    if (string.Equals(runtime.ModelPath, path, StringComparison.OrdinalIgnoreCase))
        await runtime.ActivateAsync(null, cancellationToken);

    File.Delete(path);
    return Results.Ok(new { deleted = Path.GetFileName(path) });
});

app.MapGet("/v1/files", () =>
{
    if (!Directory.Exists(options.FilesFolder))
        return Results.Json(Array.Empty<object>());

    var files = Directory
        .EnumerateFiles(options.FilesFolder, "*", SearchOption.AllDirectories)
        .Select(path => new
        {
            path = Path.GetRelativePath(options.FilesFolder, path).Replace('\\', '/'),
            fullPath = path,
            bytes = new FileInfo(path).Length,
            modifiedUtc = File.GetLastWriteTimeUtc(path),
        })
        .OrderBy(entry => entry.path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Json(files);
});

app.MapPut("/v1/files/{**path}", async (
    string path,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!HostFiles.TryResolve(options.FilesFolder, path, out string destination))
        return Results.BadRequest(new { error = new { message = "Invalid file path." } });

    long bytes;
    try
    {
        bytes = await HostFiles.SaveAsync(context.Request.Body, destination, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        return Results.Empty;
    }

    if (bytes == 0)
    {
        try { File.Delete(destination); } catch { }
        return Results.BadRequest(new { error = new { message = "The upload was empty." } });
    }

    return Results.Json(new
    {
        path = Path.GetRelativePath(options.FilesFolder, destination).Replace('\\', '/'),
        fullPath = destination,
        bytes,
    });
});

app.MapGet("/v1/files/{**path}", (string path) =>
{
    if (!HostFiles.TryResolve(options.FilesFolder, path, out string full) || !File.Exists(full))
        return Results.NotFound();

    return Results.File(
        full,
        "application/octet-stream",
        Path.GetFileName(full),
        enableRangeProcessing: true);
});

app.MapDelete("/v1/files/{**path}", (string path) =>
{
    if (!HostFiles.TryResolve(options.FilesFolder, path, out string full) || !File.Exists(full))
        return Results.NotFound();

    File.Delete(full);
    return Results.Ok(new { deleted = Path.GetRelativePath(options.FilesFolder, full).Replace('\\', '/') });
});

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

app.MapGet("/v1/training/jobs/{id}/download", (
    string id,
    string? artifact,
    TrainingJobManager jobs) =>
{
    if (jobs.Get(id) is not { } state) return Results.NotFound();

    string? path = artifact?.Trim().ToLowerInvariant() switch
    {
        "adapter" => state.AdapterPath,
        _ => state.ExportPath,
    };

    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return Results.NotFound(new { error = new { message = "Artifact is not available yet." } });

    return Results.File(
        path,
        "application/octet-stream",
        Path.GetFileName(path),
        enableRangeProcessing: true);
});

app.Run();

static object[] ListedModels(ModelRuntime runtime, ComputeOptions options)
{
    if (!Directory.Exists(options.ModelsFolder)) return [];

    string? active = runtime.ModelPath is { } current
        ? Path.GetFullPath(current)
        : null;

    return Directory
        .EnumerateFiles(options.ModelsFolder)
        .Where(options.IsSupportedModelName)
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .Select(path => (object)new
        {
            id = Path.GetFileNameWithoutExtension(path),
            name = Path.GetFileName(path),
            @object = "model",
            owned_by = "sharpmind",
            bytes = new FileInfo(path).Length,
            active = active is not null
                && string.Equals(Path.GetFullPath(path), active, StringComparison.OrdinalIgnoreCase),
        })
        .ToArray();
}

public partial class Program;
