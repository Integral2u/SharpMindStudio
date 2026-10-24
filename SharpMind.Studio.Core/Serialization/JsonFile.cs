using System.Text.Json;

namespace SharpMind.Studio.Core.Serialization;

/// <summary>Small file helpers that keep writes atomic and reads fail loudly.</summary>
public static class JsonFile
{
    public static T Read<T>(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, StudioJson.File)
            ?? throw new InvalidDataException($"'{path}' contains no JSON value.");
    }

    public static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, StudioJson.File)
        ?? throw new InvalidDataException("The JSON document contains no value.");

    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, StudioJson.File));
        File.Move(temp, path, overwrite: true);
    }
}
