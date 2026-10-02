using ItemCopy.Config;

namespace ItemCopy.Pipeline;

public enum ItemOutcome
{
    /// <summary>Not processed yet.</summary>
    Pending,

    /// <summary>Loaded into the destination with no validation errors; temporary files cleaned up.</summary>
    Ok,

    /// <summary>Loaded, but with validation errors. The .raif blob is kept for investigation.</summary>
    Partial,

    Failed,

    /// <summary>Not attempted because an earlier item failed or the run was cancelled.</summary>
    Skipped,
}

public sealed class ItemResult(JobItem item, Guid transferId)
{
    public JobItem Item { get; } = item;
    public Guid TransferId { get; } = transferId;
    public ItemOutcome Outcome { get; set; } = ItemOutcome.Pending;
    public string? Error { get; set; }

    public bool SourceTransferCreated { get; set; }
    public bool SourceTransferDeleted { get; set; }

    /// <summary>Creating the source transfer failed in a way that doesn't tell us whether it exists.</summary>
    public bool SourceTransferUncertain { get; set; }

    public int? TotalItems { get; set; }
    public int? TransferredItems { get; set; }
    public List<string> ValidationErrors { get; } = [];

    /// <summary>.raif blobs generated in the destination for this item.</summary>
    public List<string> Blobs { get; } = [];
    public HashSet<string> DeletedBlobs { get; } = [];

    public IEnumerable<string> LeftoverBlobs => Blobs.Where(b => !DeletedBlobs.Contains(b));

    public bool LeftoverSourceTransfer => SourceTransferCreated && !SourceTransferDeleted;
}

public sealed class RunResult(IReadOnlyList<ItemResult> items, bool cancelled)
{
    public IReadOnlyList<ItemResult> Items { get; } = items;
    public bool Cancelled { get; } = cancelled;

    /// <summary>0 = all ok, 1 = failure or cancelled, 2 = finished but at least one item partial.</summary>
    public int ExitCode =>
        Cancelled || Items.Any(i => i.Outcome is ItemOutcome.Failed or ItemOutcome.Skipped or ItemOutcome.Pending) ? 1
        : Items.Any(i => i.Outcome == ItemOutcome.Partial) ? 2
        : 0;
}
