using ItemCopy.Api;
using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Http;

namespace ItemCopy.Pipeline;

public sealed class RunnerSettings
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time for a wait (export, blob ready), or for a load to go without progress.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>How long to keep looking for the load's transfer ID after starting it.</summary>
    public TimeSpan TransferLookupTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public int ChunkParallelism { get; init; } = 4;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public Func<Guid> NewTransferId { get; init; } = Guid.NewGuid;
}

/// <summary>
/// Runs a job: one content transfer per item. Each item is exported, copied, and loaded into the
/// destination strictly in job order, and its transfer is only created when its turn comes.
/// </summary>
public sealed class TransferRunner(ContentTransferClient content, ItemTransferClient items, Ui ui, RunnerSettings settings)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    public Task<RunResult> RunAsync(Job job, SitecoreEnvironment source, SitecoreEnvironment destination, CancellationToken ct)
    {
        return WithSourceTransfersAsync(job, source, async results =>
        {
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                ui.Plain();
                ui.Step($"[{i + 1}/{results.Count}] {result.Item.Path} ({result.Item.Scope}, {result.Item.MergeStrategy})");

                // Created only now: loads are slow, so a transfer created up front could sit on the source for hours.
                await CreateSourceTransferAsync(result, job, source, ct);
                if (result.Outcome == ItemOutcome.Failed)
                    break;

                try
                {
                    await ProcessItemAsync(result, job, source, destination, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Fail(result, ex);
                    break;
                }
            }
        }, ct);
    }

    /// <summary>
    /// Dry run: exports every item from the source to check its path and count its items, then deletes
    /// the exports. Nothing is sent to the destination.
    /// </summary>
    public Task<RunResult> PreviewAsync(Job job, SitecoreEnvironment source, CancellationToken ct)
    {
        ui.Step($"Exporting {job.Items.Count} item(s) from {source.Name} to count them...");
        return WithSourceTransfersAsync(job, source, async results =>
        {
            await Task.WhenAll(results.Select(r => CreateSourceTransferAsync(r, job, source, ct)));
            foreach (var result in results.Where(r => r.Outcome != ItemOutcome.Failed))
            {
                ui.Plain();
                ui.Step(result.Item.Path);
                try
                {
                    var chunkSets = await WaitForExportAsync(result, job, source, ct);
                    result.TotalItems = chunkSets.Sum(c => c.TotalItemCount);
                    result.Outcome = ItemOutcome.Ok;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Fail(result, ex); // unlike a real run, keep going: the point is to report every path
                }
            }
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="process"/>, which creates the items' source transfers. Whatever happens,
    /// including Ctrl+C, deletes the transfers still on the source and marks items that never finished.
    /// </summary>
    private async Task<RunResult> WithSourceTransfersAsync(Job job, SitecoreEnvironment source, Func<List<ItemResult>, Task> process, CancellationToken ct)
    {
        var results = job.Items.Select(i => new ItemResult(i, settings.NewTransferId())).ToList();
        var cancelled = false;

        try
        {
            await process(results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            ui.Warn("Cancelled.");
            // Said now as well as in the summary: a second Ctrl+C during cleanup exits before the summary.
            foreach (var result in results.Where(r => r.UnfinishedLoad is not null))
                ui.Warn($"{job.Destination} had already started loading {result.UnfinishedLoad} into {job.Database} and will finish it. Cancelling doesn't stop a load.");
        }
        finally
        {
            await DeleteRemainingSourceTransfersAsync(results, source);
        }

        foreach (var result in results.Where(r => r.Outcome == ItemOutcome.Pending))
            result.Outcome = result.UnfinishedLoad is null ? ItemOutcome.Skipped : ItemOutcome.Unknown;

        return new RunResult(results, cancelled);
    }

    private async Task CreateSourceTransferAsync(ItemResult result, Job job, SitecoreEnvironment source, CancellationToken ct)
    {
        // Until the source answers, assume the transfer may exist so cleanup (including after Ctrl+C) tries to delete it.
        result.SourceTransferExists = true;
        result.SourceTransferUncertain = true;
        try
        {
            await content.CreateAsync(source, result.TransferId, result.Item, job.Database, ct);
            result.SourceTransferUncertain = false;
            ui.Debug($"Created transfer {result.TransferId} for {result.Item.Path}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is SitecoreApiException { IsRejected: true })
                result.SourceTransferExists = result.SourceTransferUncertain = false;
            Fail(result, ex);
        }
    }

    private void Fail(ItemResult result, Exception ex)
    {
        result.Outcome = ItemOutcome.Failed;
        result.Error = ex.Message;
        ui.Error($"{result.Item.Path}: {ex.Message}");
    }

    private async Task ProcessItemAsync(ItemResult result, Job job, SitecoreEnvironment source, SitecoreEnvironment destination, CancellationToken ct)
    {
        try
        {
            var chunkSets = await WaitForExportAsync(result, job, source, ct);
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
        var counts = Report.Written(result) is { } written ? $" {written}." : "";
        if (anyPartial)
            ui.Warn($"{result.Item.Path}: loaded with validation errors.{counts}");
        else
            ui.Success($"{result.Item.Path}: done.{counts}");
    }

    private async Task<IReadOnlyList<ChunkSetMetadata>> WaitForExportAsync(ItemResult result, Job job, SitecoreEnvironment source, CancellationToken ct)
    {
        ui.Info($"Waiting for {source.Name} to prepare the export...");
        // Creation is accepted asynchronously, so briefly allow the transfer to be not-yet-registered.
        var notFoundDeadline = settings.Time.GetUtcNow() + settings.TransferLookupTimeout;
        var recreated = false;
        var status = await PollAsync($"the export of {result.Item.Path} on {source.Name}", settings.Timeout, async () =>
        {
            var s = await content.GetStatusAsync(source, result.TransferId, ct);
            var lost = s.State == ContentTransferState.NotFound && settings.Time.GetUtcNow() >= notFoundDeadline;
            if (lost && !recreated)
            {
                // Known Sitecore bug CFW-9663: the source can lose a transfer a few minutes after creating it.
                // Creating it again with the same ID is safe: it overwrites the transfer.
                ui.Warn($"{source.Name} lost transfer {result.TransferId}; creating it again.");
                await content.CreateAsync(source, result.TransferId, result.Item, job.Database, ct);
                recreated = true;
                notFoundDeadline = settings.Time.GetUtcNow() + settings.TransferLookupTimeout;
                return null;
            }
            return s.State switch
            {
                ContentTransferState.Completed => s,
                ContentTransferState.Failed => throw new InvalidOperationException($"{source.Name} reported the export as Failed. Check that the path exists in {source.Name}."),
                _ when lost => throw new InvalidOperationException($"{source.Name} has no transfer {result.TransferId} (NotFound), even after creating it again."),
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
        // Until the destination answers, assume the load may have started. Once it has, nothing here can stop it.
        result.UnfinishedLoad = blob;
        Uri? location;
        try
        {
            location = await items.StartConsumeAsync(destination, database, blob, ct);
        }
        catch (SitecoreApiException ex) when (ex.IsRejected)
        {
            result.UnfinishedLoad = null; // refused, so nothing is loading
            throw;
        }

        var transferId = await PollAsync($"the load of {blob} to appear on {destination.Name}", settings.TransferLookupTimeout,
            () => items.ResolveTransferIdAsync(destination, location, blob, ct), ct);
        result.LoadTransferId = transferId;
        ui.Debug($"Load transfer ID: {transferId}");

        // After a retry, a Failed state only counts once the retry has visibly started (a non-Failed state
        // or a new transfer ID), or the lookup grace period has passed. The retry may re-queue
        // asynchronously or create a new transfer record.
        DateTimeOffset? retriedAt = null;
        var retryStarted = false;
        int? written = null;
        // A large load can take hours (roughly 4 items/second), so only give up once it stops making progress.
        var transfer = await PollAsync($"the load of {blob} on {destination.Name}", settings.Timeout, async () =>
        {
            var t = await items.GetTransferAsync(destination, transferId, ct);
            written = t?.TransferredItemsCount ?? written;
            if (retriedAt is not null && t?.TransferState is { } state && state != ItemTransferState.Failed)
                retryStarted = true;

            switch (t?.TransferState)
            {
                case ItemTransferState.Failed when retriedAt is null:
                    ui.Warn($"Loading {blob} failed{Describe(t)}; retrying once.");
                    await items.RetryAsync(destination, database, blob, ct);
                    retriedAt = settings.Time.GetUtcNow();
                    return null;
                case ItemTransferState.Failed when !retryStarted && settings.Time.GetUtcNow() - retriedAt < settings.TransferLookupTimeout:
                    if (await items.FindTransferIdBySourceAsync(destination, blob, ct) is { } newest && newest != transferId)
                    {
                        ui.Debug($"Retry created load transfer {newest}");
                        transferId = result.LoadTransferId = newest;
                        retryStarted = true;
                    }
                    return null;
                case ItemTransferState.Finished or ItemTransferState.Failed or ItemTransferState.Discarded:
                    return t;
                default:
                    return null;
            }
        }, ct, progress: () => written);

        // The destination has stopped working on the load, one way or another.
        result.UnfinishedLoad = null;
        if (transfer.TransferState == ItemTransferState.Failed)
            throw new InvalidOperationException($"Loading {blob} failed again after a retry{Describe(transfer)}.");
        if (transfer.TransferState == ItemTransferState.Discarded)
            throw new InvalidOperationException($"Loading {blob} was discarded{Describe(transfer)}.");

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
        if (!result.SourceTransferExists)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CleanupTimeout);
            await content.DeleteAsync(source, result.TransferId, timeout.Token);
            result.SourceTransferExists = false;
            ui.Debug($"Deleted transfer {result.TransferId} on {source.Name}");
        }
        catch (Exception ex)
        {
            ui.Warn($"Could not delete transfer {result.TransferId} on {source.Name}: {ex.Message}");
        }
    }

    private async Task DeleteRemainingSourceTransfersAsync(IEnumerable<ItemResult> results, SitecoreEnvironment source)
    {
        var remaining = results.Where(r => r.SourceTransferExists).ToList();
        if (remaining.Count == 0)
            return;

        ui.Info($"Deleting {remaining.Count} transfer(s) on {source.Name}...");
        await Task.WhenAll(remaining.Select(r => DeleteSourceTransferAsync(r, source, CancellationToken.None)));
    }

    /// <summary>
    /// Calls <paramref name="poll"/> every poll interval until it returns non-null, it throws, or <paramref name="timeout"/>
    /// passes. With <paramref name="progress"/>, the timeout restarts whenever its value changes.
    /// </summary>
    private async Task<T> PollAsync<T>(string what, TimeSpan timeout, Func<Task<T?>> poll, CancellationToken ct, Func<int?>? progress = null) where T : class
    {
        var deadline = settings.Time.GetUtcNow() + timeout;
        int? lastProgress = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await poll() is { } value)
                return value;
            if (progress?.Invoke() is { } current && current != lastProgress)
            {
                lastProgress = current;
                deadline = settings.Time.GetUtcNow() + timeout;
            }
            if (settings.Time.GetUtcNow() >= deadline)
                throw new TimeoutException(progress is null
                    ? $"Timed out after {timeout} waiting for {what}."
                    : $"Timed out: {what} made no progress for {timeout}.");
            await settings.Delay(settings.PollInterval, ct);
        }
    }

    private static string Describe(ItemTransferStatus? t) =>
        string.IsNullOrWhiteSpace(t?.Description) ? "" : $" ({t.Description})";
}
