using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpMind.Data.Pipeline;
using SharpMind.Data.Sources;

namespace SharpMind.Studio.Core.Training;

/// <summary>Half-open bucket range for one deterministic split.</summary>
public readonly record struct DeterministicSplitRange(int Low, int High);

/// <summary>
/// Applies a split at the source, before cleaning. Data Factory text JSONL lines
/// carry <c>source</c> and <c>id</c>; using them as the group key keeps every
/// variant of one document on the same side of the split. Sources without that
/// metadata fall back to the text hash, which is what every source did before.
/// </summary>
internal static class SplitSources
{
    public static IDataSource Apply(
        IDataSource source,
        JobComponent? component,
        int seed,
        DeterministicSplitRange range,
        bool incremental)
    {
        if (!incremental && IsDataFactoryTextJsonl(component, out string? path, out string? textField))
        {
            return new GroupedJsonlSource(path!, textField!, seed, range.Low, range.High);
        }

        return new SplitTextSource(source, seed, range.Low, range.High);
    }

    private static bool IsDataFactoryTextJsonl(
        JobComponent? component,
        out string? path,
        out string? textField)
    {
        path = null;
        textField = null;
        if (component is null) return false;
        if (!string.Equals(component.TypeName, typeof(JsonlSource).FullName, StringComparison.Ordinal))
            return false;
        if (!component.Args.TryGetValue("path", out path) || string.IsNullOrWhiteSpace(path))
            return false;

        textField = component.Args.TryGetValue("textField", out string? configured)
            ? configured
            : "text";
        return true;
    }
}

/// <summary>Filters an existing text source by the deterministic bucket of its text.</summary>
internal sealed class SplitTextSource(
    IDataSource inner,
    int seed,
    int low,
    int high) : IDataSource
{
    public long? EstimatedCount => null;

    public string Description => $"{inner.Description} split[{low},{high})";

    public async IAsyncEnumerable<string> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string document in inner.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            int bucket = DeterministicSplit.Bucket(document, seed);
            if (bucket >= low && bucket < high)
                yield return document;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Reads Data Factory text JSONL directly and keeps only the documents whose
/// <c>source</c>+<c>id</c> group falls in the requested bucket range. The text
/// is returned exactly as the normal JsonlSource would yield it, so cleaning and
/// tokenisation are unchanged.
/// </summary>
internal sealed class GroupedJsonlSource : IDataSource
{
    private readonly string[] _paths;
    private readonly string[] _fieldPath;
    private readonly int _seed;
    private readonly int _low;
    private readonly int _high;

    public GroupedJsonlSource(string path, string textField, int seed, int low, int high)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(textField);
        _paths = GlobResolver.Resolve(path);
        if (_paths.Length == 0)
            throw new FileNotFoundException($"No files matched: {path}");
        _fieldPath = textField.Split('.', StringSplitOptions.RemoveEmptyEntries);
        _seed = seed;
        _low = low;
        _high = high;
    }

    public long? EstimatedCount => null;

    public string Description =>
        _paths.Length == 1
            ? $"GroupedJsonl({Path.GetFileName(_paths[0])}, split[{_low},{_high}))"
            : $"GroupedJsonl({_paths.Length} files, split[{_low},{_high}))";

    public async IAsyncEnumerable<string> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };

        foreach (string path in _paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65_536, useAsync: true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryRead(line, options, out string text, out string groupKey)) continue;

                int bucket = DeterministicSplit.Bucket(groupKey, _seed);
                if (bucket >= _low && bucket < _high)
                    yield return text;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool TryRead(
        string line,
        JsonDocumentOptions options,
        out string text,
        out string groupKey)
    {
        text = "";
        groupKey = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(line, options);
            JsonElement current = document.RootElement;
            foreach (string key in _fieldPath)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(key, out current))
                {
                    return false;
                }
            }

            if (current.ValueKind != JsonValueKind.String) return false;
            text = current.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            string? source = document.RootElement.TryGetProperty("source", out JsonElement sourceElement)
                ? sourceElement.ToString()
                : null;
            string? id = document.RootElement.TryGetProperty("id", out JsonElement idElement)
                ? idElement.ToString()
                : null;
            groupKey = !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(id)
                ? $"{source}#{id}"
                : text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
