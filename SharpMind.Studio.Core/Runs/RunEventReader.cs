using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Runs;

/// <summary>Reads <c>events.jsonl</c> back into typed events.</summary>
public static class RunEventReader
{
    public static async IAsyncEnumerable<RunEvent> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Run event stream was not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        int lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            RunEvent? runEvent;
            try
            {
                runEvent = JsonSerializer.Deserialize<RunEvent>(line, StudioJson.Event);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Invalid run event at {Path.GetFileName(path)}:{lineNumber}: {ex.Message}", ex);
            }

            yield return runEvent
                ?? throw new InvalidDataException($"Empty run event at {Path.GetFileName(path)}:{lineNumber}.");
        }
    }
}
