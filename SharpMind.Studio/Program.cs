using SharpMind.Studio;
using SharpMind.Studio.Components;
using SharpMind.Studio.Services;
using SharpMind.Studio.Services.Chat;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.HuggingFace;
using SharpMind.Studio.Core.OpenRouter;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["Studio:Root"] is { Length: > 0 } root)
    StudioPaths.UseRoot(root);

builder.WebHost.UseStaticWebAssets();

var studioUrls = builder.Configuration["Studio:Url"] ?? "http://127.0.0.1:5117";
// The LAN port is always bound. GatewayAuthMiddleware answers other addresses with 404
// until LAN pairing is on, so the Settings toggle takes effect without a restart.
studioUrls += ";" + (builder.Configuration["Studio:LanUrl"] ?? "http://0.0.0.0:5118");
builder.WebHost.UseUrls(studioUrls);
builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection("Studio"));
builder.Services.AddSingleton<StudioWorkspaceService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<StudioActivityService>();
builder.Services.AddSingleton<StudioCanaryService>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<InferenceSettingsService>();
builder.Services.AddSingleton<OpenRouterSettingsService>();
builder.Services.AddSingleton<OpenRouterChatBackend>();
builder.Services.AddSingleton<ComputeProviderService>();
builder.Services.AddSingleton<ComputeChatBackend>();
builder.Services.AddSingleton<DockerHostService>();
builder.Services.AddSingleton<RunpodService>();
builder.Services.AddSingleton<SshHostService>();
builder.Services.AddHttpClient<OpenRouterClient>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<ComputeClient>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<RunpodClient>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<HuggingFaceClient>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<SharpMind.Studio.Core.Chat.StudioModelHost>();
builder.Services.AddSingleton<MarkdownService>();
builder.Services.AddSingleton<SystemWorkSkillsService>();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddSingleton<GatewayRateLimiter>();
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(new HttpMessageInvoker(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
}));
builder.Services.AddSingleton<WorkToolService>();
builder.Services.AddSingleton<TuneRunnerService>();
builder.Services.AddSingleton<StudioTrainingService>();
builder.Services.AddSingleton<StudioConversionService>();
builder.Services.AddSingleton<StudioExamService>();
builder.Services.AddSingleton<StudioRehearsalService>();
#if SHARPMIND_SOURCE
builder.Services.AddSingleton<StudioLayerLabService>();
builder.Services.AddSingleton<StudioQuantizationLabService>();
builder.Services.AddSingleton<StudioVisualizerService>();
#endif
builder.Services.AddScoped<StudioDeviceAccess>();
builder.Services.AddScoped<PathPickerService>();
builder.Services.AddScoped<StudioWorkService>();
builder.Services.AddScoped<StudioChatService>();
builder.Services.AddScoped<DataFactoryService>();
builder.Services.AddScoped<HuggingFaceService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseMiddleware<OriginGuardMiddleware>();
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseMiddleware<GatewayProxyMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGatewayEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();












