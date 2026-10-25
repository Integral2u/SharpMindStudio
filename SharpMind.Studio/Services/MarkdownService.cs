using Ganss.Xss;
using Markdig;

namespace SharpMind.Studio.Services;

public sealed class MarkdownService
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAdvancedExtensions()
        .Build();

    private readonly HtmlSanitizer _sanitizer = new();

    public MarkdownService()
    {
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");
    }

    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));
    }
}
