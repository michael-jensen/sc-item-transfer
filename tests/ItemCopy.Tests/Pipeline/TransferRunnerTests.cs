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
}
