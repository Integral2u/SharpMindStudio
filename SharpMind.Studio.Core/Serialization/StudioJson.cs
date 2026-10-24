using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpMind.Studio.Core.Serialization;

/// <summary>
/// One place for the JSON conventions shared by project files, Recipes and
/// run events. Web defaults give camelCase names and case-insensitive reads;
/// file JSON is indented, event JSON is one compact object per line.
/// </summary>
public static class StudioJson
{
    public static readonly JsonSerializerOptions File = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Event = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
