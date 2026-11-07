using SharpMind.Data.Records;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Reads the deterministic document group recorded by data exporters.
/// Data Factory writes a source path and document id per line; every variant of
/// one document shares them. The group is stored as a tag rather than a
/// <see cref="DataRecord"/> property so Studio still compiles against the
/// published SharpMind.Data 1.1.0 package (which predates GroupId).
/// </summary>
public static class RecordGrouping
{
    public const string TagPrefix = "group:";

    /// <summary>The group key, or null when the record has no provenance.</summary>
    public static string? GroupKey(DataRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (string tag in record.Tags)
        {
            if (tag.StartsWith(TagPrefix, StringComparison.Ordinal)
                && tag.Length > TagPrefix.Length)
                return tag[TagPrefix.Length..];
        }
        return null;
    }

    /// <summary>The stable key a train/validation/exam split should hash.</summary>
    public static string SplitKey(DataRecord record) =>
        GroupKey(record) ?? record.Text;
}
