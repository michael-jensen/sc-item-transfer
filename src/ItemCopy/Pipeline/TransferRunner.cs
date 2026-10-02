using ItemCopy.Api;
using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Http;

namespace ItemCopy.Pipeline;

public sealed class RunnerSettings
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time for any single wait (export, blob ready, load).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to keep looking for the load's transfer ID after starting it.</summary>
    public TimeSpan TransferLookupTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public int ChunkParallelism { get; init; } = 4;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public Func<Guid> NewTransferId { get; init; } = Guid.NewGuid;
}

/// <summary>
/// Runs a job: one content transfer per item, created up front in parallel, then each item is
/// exported, copied, and loaded into the destination strictly in job order.
/// </summary>
public sealed class TransferRunner(ContentTransferClient content, ItemTransferClient items, Ui ui, RunnerSettings settings)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    public async Task<RunResult> RunAsync(Job job, SitecoreEnvironment source, SitecoreEnvironment destination, CancellationToken ct)
    {
        var results = job.Items.Select(i => new ItemResult(i, settings.NewTransferId())).ToList();
        var cancelled = false;

        try
        {
            ui.Step($"Creating {results.Count} transfer(s) on {source.Name}...");
            await Task.WhenAll(results.Select(r => CreateSourceTransferAsync(r, job, source, ct)));

            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                if (result.Outcome == ItemOutcome.Failed)
                    break; // creation failed

                ui.Plain();
                ui.Step($"[{i + 1}/{results.Count}] {result.Item.Path} ({result.Item.Scope}, {result.Item.MergeStrategy})");
                try
                {
                    await ProcessItemAsync(result, job, source, destination, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result.Outcome = ItemOutcome.Failed;
                    result.Error = ex.Message;
                }

                if (result.Outcome == ItemOutcome.Failed)
                {
                    ui.Error($"{result.Item.Path}: {result.Error}");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            ui.Warn("Cancelled.");
        }
        finally
        {
            await DeleteRemainingSourceTransfersAsync(results, source);
        }

        foreach (var result in results.Where(r => r.Outcome == ItemOutcome.Pending))
            result.Outcome = ItemOutcome.Skipped;

        return new RunResult(results, cancelled);
    }

    private async Task CreateSourceTransferAsync(ItemResult result, Job job, SitecoreEnvironment source, CancellationToken ct)
    {
        try
        {
            await content.CreateAsync(source, result.TransferId, result.Item, job.Database, ct);
            result.SourceTransferCreated = true;
            ui.Debug($"Created transfer {result.TransferId} for {result.Item.Path}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A 4xx means the source rejected it; otherwise it may have been created, so cleanup tries to delete it.
            var rejected = ex is SitecoreApiException { StatusCode: { } status } && (int)status < 500;
            result.SourceTransferCreated = !rejected;
            result.SourceTransferUncertain = !rejected;
            result.Outcome = ItemOutcome.Failed;
            result.Error = ex.Message;
            ui.Error($"{result.Item.Path}: {ex.Message}");
        }
    }

    private async Task ProcessItemAsync(ItemResult result, Job job, SitecoreEnvironment source, SitecoreEnvironment destination, CancellationToken ct)
    {
        try
        {
            var chunkSets = await WaitForExportAsync(result, source, ct);
            foreach (var chunkSet in chunkSets)
            {
                await CopyChunkSetAsync(result, chunkSet, source, destination, ct);
            }
        }
        finally
        {
            // All chunks are downloaded (or the item failed); the source transfer is no longer needed.
            await DeleteSourceTransferAsync(result, source, ct.IsCancellationRequested ? CancellationToken.None : ct);
        }

        var anyPartial = false;
        foreach (var blob in result.Blobs)
        {
            anyPartial |= !await LoadBlobAsync(result, blob, job.Database, destination, ct);
        }

        result.Outcome = anyPartial ? ItemOutcome.Partial : ItemOutcome.Ok;
        var counts = result.TotalItems is { } total ? $" {result.TransferredItems ?? 0}/{total} item(s) written." : "";
        if (anyPartial)
            ui.Warn($"{result.Item.Path}: loaded with validation errors.{counts}");
        else
            ui.Success($"{result.Item.Path}: done.{counts}");
    }

    private async Task<IReadOnlyList<ChunkSetMetadata>> WaitForExportAsync(ItemResult result, SitecoreEnvironment source, CancellationToken ct)
    {
        ui.Info($"Waiting for {source.Name} to prepare the export...");
        var status = await PollAsync($"the export of {result.Item.Path} on {source.Name}", settings.Timeout, async () =>
        {
            var s = await content.GetStatusAsync(source, result.TransferId, ct);
            return s.State switch
            {
                ContentTransferState.Completed => s,
                ContentTransferState.Failed => throw new InvalidOperationException($"{source.Name} reported the export as Failed. Check that the path exists in {source.Name}."),
                ContentTransferState.NotFound => throw new InvalidOperationException($"{source.Name} has no transfer {result.TransferId} (NotFound)."),
                _ => null,
            };
        }, ct);

        var chunkSets = status.ChunkSetsMetadata ?? [];
        if (chunkSets.Count == 0)
            throw new InvalidOperationException($"{source.Name} completed the export but returned no chunk sets.");

        var itemCount = chunkSets.Sum(c => c.TotalItemCount);
        ui.Info($"Export ready: {itemCount} item(s) in {chunkSets.Sum(c => c.ChunkCount)} chunk(s).");
        return chunkSets;
    }

    private async Task CopyChunkSetAsync(ItemResult result, ChunkSetMetadata chunkSet, SitecoreEnvironment source, SitecoreEnvironment destination, CancellationToken ct)
    {
        var copied = 0;
        var skipped = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunkSet.ChunkCount),
            new ParallelOptions { MaxDegreeOfParallelism = settings.ChunkParallelism, CancellationToken = ct },
            async (chunkId, token) =>
            {
                var info = await content.CopyChunkAsync(source, destination, result.TransferId, chunkSet.ChunkSetId, chunkId, token);
                Interlocked.Add(ref skipped, info.ItemsSkipped ?? 0);
                var done = Interlocked.Increment(ref copied);
                ui.Info($"Copied chunk {done}/{chunkSet.ChunkCount}{(info.IsMedia ? " (media)" : "")}");
            });

        if (skipped > 0)
            ui.Warn($"{source.Name} skipped {skipped} item(s) in {result.Item.Path} while exporting.");

        var blob = await content.CompleteChunkSetAsync(destination, result.TransferId, chunkSet.ChunkSetId, ct);
        result.Blobs.Add(blob);
        ui.Info($"Generated {blob} on {destination.Name}.");
    }

    /// <summary>Loads one .raif blob into the destination database. Returns false if it finished with validation errors.</summary>
    private async Task<bool> LoadBlobAsync(ItemResult result, string blob, string database, SitecoreEnvironment destination, CancellationToken ct)
    {
        ui.Info($"Waiting for {blob} to be ready on {destination.Name}...");
        await PollAsync($"{blob} to be ready on {destination.Name}", settings.Timeout, async () =>
        {
            var details = await items.GetBlobAsync(destination, blob, ct);
            if (details is null || details.BlobState is null || BlobState.Pending.Contains(details.BlobState))
                return (BlobDetails?)null;
            if (details.BlobState == BlobState.Uploaded)
                return details;
            throw new InvalidOperationException($"{blob} is in state {details.BlobState}{(details.Error is { } e ? $": {e}" : "")}.");
        }, ct);

        ui.Info($"Loading {blob} into {destination.Name}/{database}...");
        var location = await items.StartConsumeAsync(destination, database, blob, ct);

        var transferId = await PollAsync($"the load of {blob} to appear on {destination.Name}", settings.TransferLookupTimeout,
            () => items.ResolveTransferIdAsync(destination, location, blob, ct), ct);
        ui.Debug($"Load transfer ID: {transferId}");

        var retried = false;
        var transfer = await PollAsync($"the load of {blob} on {destination.Name}", settings.Timeout, async () =>
        {
            var t = await items.GetTransferAsync(destination, transferId, ct);
            switch (t?.TransferState)
            {
                case ItemTransferState.Finished:
                    return t;
                case ItemTransferState.Failed when !retried:
                    retried = true;
                    ui.Warn($"Loading {blob} failed{Describe(t)}; retrying once.");
                    await items.RetryAsync(destination, database, blob, ct);
                    return null;
                case ItemTransferState.Failed:
                    throw new InvalidOperationException($"Loading {blob} failed again after a retry{Describe(t)}.");
                case ItemTransferState.Discarded:
                    throw new InvalidOperationException($"Loading {blob} was discarded{Describe(t)}.");
                default:
                    return null;
            }
        }, ct);

        result.TotalItems = (result.TotalItems ?? 0) + (transfer.TotalItemsCount ?? 0);
        result.TransferredItems = (result.TransferredItems ?? 0) + (transfer.TransferredItemsCount ?? 0);

        var blobState = (await items.GetBlobAsync(destination, blob, ct))?.BlobState;
        var errors = transfer.ValidationErrors ?? [];
        if (errors.Count > 0 || blobState == BlobState.TransferredWithErrors)
        {
            result.ValidationErrors.AddRange(errors);
            foreach (var error in errors)
                ui.Warn($"  {error}");
            ui.Warn($"Keeping {blob} on {destination.Name} for investigation.");
            return false;
        }

        try
        {
            await items.DeleteBlobAsync(destination, blob, ct);
            result.DeletedBlobs.Add(blob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ui.Warn($"Could not delete {blob}: {ex.Message}");
        }
        return true;
    }

    private async Task DeleteSourceTransferAsync(ItemResult result, SitecoreEnvironment source, CancellationToken ct)
    {
        if (!result.SourceTransferCreated || result.SourceTransferDeleted)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CleanupTimeout);
            await content.DeleteAsync(source, result.TransferId, timeout.Token);
            result.SourceTransferDeleted = true;
            ui.Debug($"Deleted transfer {result.TransferId} on {source.Name}");
        }
        catch (Exception ex)
        {
            ui.Warn($"Could not delete transfer {result.TransferId} on {source.Name}: {ex.Message}");
        }
    }

    private async Task DeleteRemainingSourceTransfersAsync(IEnumerable<ItemResult> results, SitecoreEnvironment source)
    {
        var remaining = results.Where(r => r.LeftoverSourceTransfer).ToList();
        if (remaining.Count == 0)
            return;

        ui.Info($"Cleaning up {remaining.Count} unused transfer(s) on {source.Name}...");
        await Task.WhenAll(remaining.Select(r => DeleteSourceTransferAsync(r, source, CancellationToken.None)));
    }

    /// <summary>Calls <paramref name="poll"/> every poll interval until it returns non-null, it throws, or <paramref name="timeout"/> passes.</summary>
    private async Task<T> PollAsync<T>(string what, TimeSpan timeout, Func<Task<T?>> poll, CancellationToken ct) where T : class
    {
        var deadline = settings.Time.GetUtcNow() + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await poll() is { } value)
                return value;
            if (settings.Time.GetUtcNow() >= deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
            await settings.Delay(settings.PollInterval, ct);
        }
    }

    private static string Describe(ItemTransferStatus? t) =>
        string.IsNullOrWhiteSpace(t?.Description) ? "" : $" ({t.Description})";
}
