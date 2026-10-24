using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Projects;

public sealed record ModelList
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Models { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ModelListStore
{
    public ModelListStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public IReadOnlyList<ModelList> List()
    {
        var lists = new List<ModelList>();
        foreach (string file in Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                lists.Add(JsonFile.Read<ModelList>(file));
            }
            catch
            {
            }
        }

        return [.. lists.OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public ModelList Save(string name, IEnumerable<string> models)
    {
        string cleanName = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new ArgumentException("A list name is required.", nameof(name));

        string[] paths = [.. models
            .Select(path => path?.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Length == 0)
            throw new ArgumentException("A list needs at least one model path.", nameof(models));

        if (cleanName.Length > 80) cleanName = cleanName[..80];
        var list = new ModelList
        {
            Name = cleanName,
            Models = paths,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        JsonFile.Write(PathFor(cleanName), list);
        return list;
    }

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name) => Path.Combine(Root, Slug(name) + ".json");

    private static string Slug(string name)
    {
        var chars = name.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '-').ToArray();
        string slug = new(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "list" : slug;
    }
}
