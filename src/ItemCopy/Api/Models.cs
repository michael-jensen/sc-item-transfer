using System.Text.Json;
using System.Text.Json.Serialization;
using ItemCopy.Config;

namespace ItemCopy.Api;

public static class ApiJson
{
    /// <summary>The APIs use PascalCase names, which match the C# property names as written.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

// ---- Content Transfer API ----

public sealed record CreateContentTransferRequest(Guid TransferId, TransferConfiguration Configuration);

public sealed record TransferConfiguration(IReadOnlyList<DataTree> DataTrees, string Database);

public sealed record DataTree(string ItemPath, TransferScope Scope, MergeStrategy MergeStrategy);

public static class ContentTransferState
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string NotFound = "NotFound";
}

public sealed class ContentTransferStatus
{
    public string? State { get; set; }
    public List<ChunkSetMetadata>? ChunkSetsMetadata { get; set; }
}

public sealed class ChunkSetMetadata
{
    public Guid ChunkSetId { get; set; }
    public int ChunkCount { get; set; }
    public int TotalItemCount { get; set; }
}

public sealed class ChunkSetCompleteResponse
{
    public string? ContentTransferFileName { get; set; }
}

// ---- Item Transfer API ----

public static class BlobState
{
    public const string Uploaded = "Uploaded";
    public const string Error = "Error";
    public const string Discarded = "Discarded";
    public const string TransferredWithErrors = "TransferredWithErrors";

    /// <summary>States that mean the blob isn't ready yet but may become ready.</summary>
    public static readonly IReadOnlySet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown", "Uploading", "Initializing", "Queued",
    };
}

public static class ItemTransferState
{
    public const string Finished = "Finished";
    public const string Failed = "Failed";
    public const string Discarded = "Discarded";
}

public sealed class BlobDetails
{
    public string? BlobState { get; set; }
    public string? Error { get; set; }
    public string? SourceName { get; set; }
}

public class ItemTransferStatus
{
    public string? Id { get; set; }
    public string? SourceName { get; set; }
    public string? DatabaseName { get; set; }
    public DateTimeOffset? ConsumedDate { get; set; }
    public string? TransferState { get; set; }
    public string? Strategy { get; set; }
    public string? Description { get; set; }
}

public sealed class ItemTransferDetails : ItemTransferStatus
{
    public int? TotalItemsCount { get; set; }
    public int? TransferredItemsCount { get; set; }
    public List<string>? ValidationErrors { get; set; }
}

public sealed class ItemTransfersPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public List<ItemTransferStatus>? Transfers { get; set; }
}
