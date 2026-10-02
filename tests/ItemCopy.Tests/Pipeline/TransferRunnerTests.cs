using System.Net;
using ItemCopy.Api;
using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Http;
using ItemCopy.Pipeline;
using ItemCopy.Tests.Fakes;

namespace ItemCopy.Tests.Pipeline;

public class TransferRunnerTests
{
    private const string Home = "/sitecore/content/MySite/Home";
    private const string Media = "/sitecore/media library/Project/MySite";
    private const string Footer = "/sitecore/content/MySite/Data/Footer";

    private readonly FakeSitecore _fake = new();
    private readonly ManualTime _time = new();
    private readonly StringWriter _log = new();
    private readonly SitecoreEnvironment _source = new("DEV", FakeSitecore.SourceHost, "dev-client", "secret");
    private readonly SitecoreEnvironment _destination = new("SIT", FakeSitecore.DestinationHost, "sit-client", "secret");

    private Task<RunResult> RunAsync(params JobItem[] items) => RunAsync(CancellationToken.None, null, items);

    private Task<RunResult> RunAsync(CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay, params JobItem[] items)
    {
        var httpClient = new HttpClient(_fake);
        var ui = new Ui(_log, TextReader.Null, interactive: false, verbose: true, color: false);
        var tokens = new TokenProvider(httpClient, FakeSitecore.AuthUrl, "https://api.test", _time);
        var http = new SitecoreHttp(httpClient, tokens, new RetryPolicy { Delay = _time.Delay }, ui);
        var runner = new TransferRunner(
            new ContentTransferClient(http, ui),
            new ItemTransferClient(http),
            ui,
            new RunnerSettings { Time = _time, Delay = delay ?? _time.Delay, Timeout = TimeSpan.FromMinutes(5) });

        return runner.RunAsync(new Job("DEV", "SIT", "master", items), _source, _destination, ct);
    }

    private static JobItem Item(string path, TransferScope scope = TransferScope.ItemAndDescendants, MergeStrategy merge = MergeStrategy.OverrideExistingItem) =>
        new(path, scope, merge);

    [Fact]
    public async Task Copies_and_loads_items_in_job_order_and_cleans_up()
    {
        _fake.ChunkCounts[Home] = 3;
        _fake.ChunkCounts[Media] = 2;

        var run = await RunAsync(Item(Home), Item(Media, merge: MergeStrategy.KeepExistingItem), Item(Footer, TransferScope.SingleItem));

        Assert.Equal(0, run.ExitCode);
        Assert.All(run.Items, r => Assert.Equal(ItemOutcome.Ok, r.Outcome));
        Assert.Equal([Home, Media, Footer], _fake.LoadOrder);

        // One transfer per item, configured as written in the job.
        Assert.Equal(3, _fake.SourceTransfers.Count);
        var media = _fake.SourceTransfers.Values.Single(t => t.Path == Media);
        Assert.Equal(("ItemAndDescendants", "KeepExistingItem", "master"), (media.Scope, media.MergeStrategy, media.Database));
        Assert.Equal("SingleItem", _fake.SourceTransfers.Values.Single(t => t.Path == Footer).Scope);

        // Every chunk forwarded byte-for-byte with the right isMedia flag.
        Assert.Equal(3 + 2 + 2, _fake.SavedChunks.Count);
        foreach (var ((transferId, _, chunkId), saved) in _fake.SavedChunks)
        {
            var path = _fake.SourceTransfers[transferId].Path;
            Assert.Equal(FakeSitecore.ChunkBytes(path, chunkId), saved.Body);
            Assert.Equal(FakeSitecore.IsMediaPath(path), saved.IsMedia);
            Assert.Equal("application/octet-stream", saved.ContentType);
        }

        Assert.All(_fake.SourceTransfers.Values, t => Assert.True(t.Deleted));
        Assert.Empty(_fake.Blobs);
        Assert.All(run.Items, r => Assert.Null(r.UnfinishedLoad));
        Assert.Equal(30, run.Items[0].TotalItems);
        Assert.Equal(30, run.Items[0].TransferredItems);
        Assert.Equal(1, _fake.TokensIssued["dev-client"]);
        Assert.Equal(1, _fake.TokensIssued["sit-client"]);
    }

    [Fact]
    public async Task Retries_a_chunk_after_transient_errors()
    {
        _fake.ChunkCounts[Home] = 1;
        _fake.ChunkGetFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        _fake.ChunkGetFailures.Enqueue(HttpStatusCode.BadGateway);

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
        Assert.Single(_fake.SavedChunks);
        Assert.Contains("retrying", _log.ToString());
    }

    [Fact]
    public async Task Gives_up_on_a_chunk_after_max_retries()
    {
        _fake.ChunkCounts[Home] = 1;
        for (var i = 0; i < 4; i++)
            _fake.ChunkGetFailures.Enqueue(HttpStatusCode.InternalServerError);

        var run = await RunAsync(Item(Home));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(ItemOutcome.Failed, run.Items[0].Outcome);
        Assert.Contains("HTTP 500", run.Items[0].Error);
        Assert.True(_fake.SourceTransfers.Values.Single().Deleted);
    }

    [Fact]
    public async Task Refreshes_rejected_tokens_on_source_and_destination()
    {
        _fake.ChunkCounts[Home] = 1;
        _fake.RejectFirstRequestTo.Add(FakeSitecore.SourceHost);      // create POST: refreshed in place
        _fake.RejectFirstRequestTo.Add(FakeSitecore.DestinationHost); // chunk PUT: retried as a unit

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, _fake.TokensIssued["dev-client"]);
        Assert.Equal(2, _fake.TokensIssued["sit-client"]);
    }

    [Fact]
    public async Task Finds_load_transfer_when_location_holds_blob_name()
    {
        _fake.LocationIsBlobName = true;

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(_fake.Requests, r => r.EndsWith($"/ItemsTransfer/transfers"));
    }

    [Fact]
    public async Task Retries_a_failed_load_once()
    {
        _fake.LoadFailsOnce.Add(Home);

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
        Assert.True(_fake.ItemTransfers.Values.Single().Retried);
        Assert.Contains("retrying once", _log.ToString());
    }

    [Fact]
    public async Task Validation_errors_make_item_partial_and_keep_blob()
    {
        _fake.LoadValidationErrors[Media] = ["Item {abc} has an unknown template"];

        var run = await RunAsync(Item(Home), Item(Media), Item(Footer));

        Assert.Equal(2, run.ExitCode);
        Assert.Equal([ItemOutcome.Ok, ItemOutcome.Partial, ItemOutcome.Ok], run.Items.Select(r => r.Outcome));
        Assert.Equal(["Item {abc} has an unknown template"], run.Items[1].ValidationErrors);
        Assert.Single(run.Items[1].LeftoverBlobs);
        Assert.Single(_fake.Blobs);
        Assert.Equal([Home, Media, Footer], _fake.LoadOrder);
    }

    [Fact]
    public async Task Failed_export_stops_the_run_and_cleans_up_later_transfers()
    {
        _fake.ExportFails.Add(Home);

        var run = await RunAsync(Item(Home), Item(Media));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal([ItemOutcome.Failed, ItemOutcome.Skipped], run.Items.Select(r => r.Outcome));
        Assert.Contains("Failed", run.Items[0].Error);
        Assert.Empty(_fake.LoadOrder);
        Assert.Empty(_fake.SavedChunks);
        Assert.All(_fake.SourceTransfers.Values, t => Assert.True(t.Deleted));
        Assert.All(run.Items, r => Assert.False(r.LeftoverSourceTransfer));
    }

    [Fact]
    public async Task Rejected_create_runs_earlier_items_and_is_not_left_behind()
    {
        _fake.CreateRejects.Add(Media);

        var run = await RunAsync(Item(Home), Item(Media), Item(Footer));

        Assert.Equal([ItemOutcome.Ok, ItemOutcome.Failed, ItemOutcome.Skipped], run.Items.Select(r => r.Outcome));
        Assert.Contains("Invalid item path", run.Items[1].Error);
        Assert.Equal([Home], _fake.LoadOrder);
        Assert.All(_fake.SourceTransfers.Values, t => Assert.True(t.Deleted));
        Assert.All(run.Items, r => Assert.False(r.LeftoverSourceTransfer));
        Assert.DoesNotContain(_fake.Requests, r => r.StartsWith("DELETE") && r.Contains(run.Items[1].TransferId.ToString()));
    }

    [Fact]
    public async Task Cancelling_during_create_still_deletes_transfers_that_reached_the_source()
    {
        using var cts = new CancellationTokenSource();
        _fake.CancelAfterCreate = cts;

        var run = await RunAsync(cts.Token, null, Item(Home));

        Assert.True(run.Cancelled);
        Assert.True(_fake.SourceTransfers.Values.Single().Deleted);
        Assert.False(run.Items[0].LeftoverSourceTransfer);
    }

    [Fact]
    public async Task Tolerates_transfer_not_being_visible_right_after_create()
    {
        _fake.StatusNotFoundPolls = 2;

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task Follows_a_retry_that_creates_a_new_load_transfer()
    {
        _fake.LoadFailsOnce.Add(Home);
        _fake.RetryCreatesNewTransfer = true;

        var run = await RunAsync(Item(Home));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, _fake.ItemTransfers.Count);
        Assert.Contains(_fake.ItemTransfers.Values, t => t.Retried && t.State == "Finished");
    }

    [Fact]
    public async Task Asks_for_binary_chunks_and_refuses_json_bodies()
    {
        _fake.ChunkCounts[Home] = 1;
        _fake.ChunkContentType = "application/json";

        var run = await RunAsync(Item(Home));

        Assert.Equal(ItemOutcome.Failed, run.Items[0].Outcome);
        Assert.Contains("instead of a binary stream", run.Items[0].Error);
        Assert.Empty(_fake.SavedChunks);
        Assert.All(_fake.ChunkAcceptHeaders, a => Assert.StartsWith("application/octet-stream", a));
    }

    [Fact]
    public async Task Times_out_waiting_for_export()
    {
        _fake.ExportNeverCompletes.Add(Home);

        var run = await RunAsync(Item(Home));

        Assert.Equal(ItemOutcome.Failed, run.Items[0].Outcome);
        Assert.Contains("Timed out", run.Items[0].Error);
        Assert.True(_fake.SourceTransfers.Values.Single().Deleted);
    }

    [Fact]
    public async Task Cancellation_deletes_source_transfers()
    {
        _fake.ExportNeverCompletes.Add(Home);
        using var cts = new CancellationTokenSource();
        var delays = 0;

        // Cancel on the third poll wait, as if the user pressed Ctrl+C while waiting for the export.
        var run = await RunAsync(cts.Token, (d, ct) =>
        {
            if (++delays == 3)
                cts.Cancel();
            return _time.Delay(d, ct);
        }, Item(Home), Item(Media));

        Assert.True(run.Cancelled);
        Assert.Equal(1, run.ExitCode);
        Assert.All(_fake.SourceTransfers.Values, t => Assert.True(t.Deleted));
        Assert.All(run.Items, r => Assert.Equal(ItemOutcome.Skipped, r.Outcome));
    }

    [Fact]
    public async Task Cancelling_during_a_load_reports_the_item_as_unknown_not_skipped()
    {
        _fake.LoadNeverFinishes.Add(Home);
        using var cts = new CancellationTokenSource();

        // Cancel once the destination has started loading, as if the user pressed Ctrl+C mid-load.
        var run = await RunAsync(cts.Token, (d, ct) =>
        {
            if (_fake.LoadOrder.Count > 0)
                cts.Cancel();
            return _time.Delay(d, ct);
        }, Item(Home), Item(Media));

        Assert.True(run.Cancelled);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal([ItemOutcome.Unknown, ItemOutcome.Skipped], run.Items.Select(r => r.Outcome));
        var load = _fake.ItemTransfers.Values.Single();
        Assert.Equal(load.BlobName, run.Items[0].UnfinishedLoad);
        Assert.Equal(load.Id, run.Items[0].LoadTransferId);
        Assert.Contains("Cancelling doesn't stop", _log.ToString());
        Assert.All(_fake.SourceTransfers.Values, t => Assert.True(t.Deleted));
    }

    [Fact]
    public async Task Timing_out_on_a_load_leaves_it_marked_unfinished()
    {
        _fake.LoadNeverFinishes.Add(Home);

        var run = await RunAsync(Item(Home));

        Assert.Equal(ItemOutcome.Failed, run.Items[0].Outcome);
        Assert.Contains("Timed out", run.Items[0].Error);
        Assert.Equal(_fake.ItemTransfers.Values.Single().BlobName, run.Items[0].UnfinishedLoad);
    }

    [Fact]
    public async Task Rejected_load_is_not_marked_unfinished()
    {
        _fake.ConsumeRejects.Add(Home);

        var run = await RunAsync(Item(Home));

        Assert.Equal(ItemOutcome.Failed, run.Items[0].Outcome);
        Assert.Contains("read-only", run.Items[0].Error);
        Assert.Null(run.Items[0].UnfinishedLoad);
    }
}
