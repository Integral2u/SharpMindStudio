using SharpMind.Studio;
using SharpMind.Studio.Components;
using SharpMind.Studio.Services;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Gateway;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["Studio:Root"] is { Length: > 0 } root)
    StudioPaths.UseRoot(root);

builder.WebHost.UseStaticWebAssets();

var studioUrls = builder.Configuration["Studio:Url"] ?? "http://127.0.0.1:5117";
bool lanEnabled = builder.Configuration.GetValue<bool>("Studio:LanEnabled") ||
    new GatewayStore(StudioPaths.Gateway).LoadConfig().LanEnabled;
if (lanEnabled)
    studioUrls += ";" + (builder.Configuration["Studio:LanUrl"] ?? "http://0.0.0.0:5118");
builder.WebHost.UseUrls(studioUrls);
builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection("Studio"));
builder.Services.AddSingleton<StudioWorkspaceService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<StudioActivityService>();
builder.Services.AddSingleton<StudioCanaryService>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<InferenceSettingsService>();
builder.Services.AddSingleton<SharpMind.Studio.Core.Chat.StudioModelHost>();
builder.Services.AddSingleton<MarkdownService>();
builder.Services.AddSingleton<SystemWorkSkillsService>();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddSingleton<WorkToolService>();
builder.Services.AddSingleton<TuneRunnerService>();
builder.Services.AddScoped<StudioDeviceAccess>();
builder.Services.AddScoped<StudioWorkService>();
builder.Services.AddScoped<StudioChatService>();

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
app.UseMiddleware<GatewayAuthMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGatewayEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();












