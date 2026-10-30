using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>Read-only web access for Work. The project's Web permission gates every request.</summary>
public sealed class WorkWebTools(Func<WorkToolAccess, Task<bool>>? permission = null)
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    [ToolDesc("Fetches a public http or https URL as text. Use this only when the project allows web access.")]
    public async Task<string> FetchUrl(
        [ToolDesc("Absolute http or https URL.")] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "URL must be an absolute http or https address.";

        if (permission is null ||
            !await permission(new WorkToolAccess(nameof(FetchUrl), WorkToolCategory.Web, uri.ToString())))
            return $"Permission denied: FetchUrl may not access '{url}' with the current project permission.";

        try
        {
            using HttpResponseMessage response = await Client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            char[] buffer = new char[512 * 1024];
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            string text = new(buffer, 0, read);
            return read >= buffer.Length
                ? text + "\n\n[truncated at 512 KB]"
                : text;
        }
        catch (Exception ex)
        {
            return $"Error fetching URL: {ex.Message}";
        }
    }
}
