using System.IO.Abstractions;

namespace SharpMind.Studio.Services;

/// <summary>
/// The per-session IO gates a plugin should use. A plugin built against these
/// passes through the same file and network permission checks as a built-in
/// tool, because the chat session activates them around every tool call.
/// </summary>
public static class PluginRuntime
{
    private static IFileSystem? _files;
    private static HttpClient? _http;

    public static IFileSystem? Files => _files;

    public static HttpClient? Http => _http;

    internal static void Set(IFileSystem files, HttpMessageHandler handler)
    {
        _files = files;
        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    internal static void Clear()
    {
        _files = null;
        _http?.Dispose();
        _http = null;
    }
}
