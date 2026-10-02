using System.Text.Json;

namespace ItemCopy.Config;

/// <summary>Values match the Content Transfer API's <c>Scope</c> enum.</summary>
public enum TransferScope
{
    SingleItem,
    ItemAndDescendants,
}

/// <summary>Values match the Content Transfer API's <c>MergeStrategy</c> enum.</summary>
public enum MergeStrategy
{
    OverrideExistingItem,
    KeepExistingItem,
    OverrideExistingTree,
}

public sealed record JobItem(string Path, TransferScope Scope, MergeStrategy MergeStrategy);

/// <summary>A validated job, ready to run.</summary>
public sealed record Job(string Source, string Destination, string Database, IReadOnlyList<JobItem> Items);

/// <summary>The job file as written by the user, before validation. Enum values are kept as strings so typos get a clear message.</summary>
public sealed class JobFile
{
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public string? Database { get; set; }
    public List<JobFileItem?>? Items { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JobFile Parse(string json, string displayName)
    {
        try
        {
            return JsonSerializer.Deserialize<JobFile>(json, Options)
                ?? throw new ConfigException($"Job file {displayName} is empty.");
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            throw new ConfigException($"Job file {displayName} is not valid JSON{where}: {ex.Message}");
        }
    }

    public static JobFile Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Job file not found: {path}");
        return Parse(File.ReadAllText(path), path);
    }
}

public sealed class JobFileItem
{
    public string? Path { get; set; }
    public string? Scope { get; set; }
    public string? MergeStrategy { get; set; }
}
