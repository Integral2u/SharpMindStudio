using SharpMind.Studio;
using SharpMind.Studio.Components;
using SharpMind.Studio.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.WebHost.UseUrls(builder.Configuration["Studio:Url"] ?? "http://127.0.0.1:5117");
builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection("Studio"));
builder.Services.AddSingleton<StudioWorkspaceService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<StudioActivityService>();
builder.Services.AddSingleton<StudioCanaryService>();

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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();



