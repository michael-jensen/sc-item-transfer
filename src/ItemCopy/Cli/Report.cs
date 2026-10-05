using ItemCopy.Config;
using ItemCopy.Pipeline;

namespace ItemCopy.Cli;

/// <summary>Prints the plan before a run and the summary after it.</summary>
public static class Report
{
    /// <summary>Observed load speed on SitecoreAI (community measurements, 2026). Real speed varies.</summary>
    private const double LoadItemsPerSecond = 4;

    public static void Plan(Job job, SitecoreEnvironment source, SitecoreEnvironment destination, bool destinationProtected, IReadOnlyList<string> warnings, Ui ui)
    {
        ui.Plain();
        ui.Plain($"Source:      {source}");
        ui.Plain($"Destination: {destination}{(destinationProtected ? "  [protected]" : "")}", destinationProtected ? Ui.Ansi.BoldRed : null);
        ui.Plain($"Database:    {job.Database}");
        ui.Plain();

        var scopeWidth = Math.Max("Scope".Length, job.Items.Max(i => i.Scope.ToString().Length));
        var mergeWidth = Math.Max("Merge strategy".Length, job.Items.Max(i => i.MergeStrategy.ToString().Length));
        ui.Plain($"  #  {"Scope".PadRight(scopeWidth)}  {"Merge strategy".PadRight(mergeWidth)}  Path", Ui.Ansi.Bold);

        for (var i = 0; i < job.Items.Count; i++)
        {
            var item = job.Items[i];
            var destructive = item.MergeStrategy == MergeStrategy.OverrideExistingTree;
            var line = $"{i + 1,3}  {item.Scope.ToString().PadRight(scopeWidth)}  {item.MergeStrategy.ToString().PadRight(mergeWidth)}  {item.Path}";
            ui.Plain(destructive ? $"{line}  ← deletes existing destination subtree" : line, destructive ? Ui.Ansi.Red : null);
        }

        if (warnings.Count > 0)
        {
            ui.Plain();
            foreach (var warning in warnings)
                ui.Plain($"WARNING: {warning}", Ui.Ansi.Yellow);
        }
        ui.Plain();
    }

    public static void Summary(RunResult run, SitecoreEnvironment source, SitecoreEnvironment destination, Ui ui)
    {
        ui.Plain();
        ui.Plain("Summary", Ui.Ansi.Bold);

        foreach (var result in run.Items)
        {
            var (label, style) = result.Outcome switch
            {
                ItemOutcome.Ok => ("OK     ", Ui.Ansi.Green),
                ItemOutcome.Partial => ("PARTIAL", Ui.Ansi.Yellow),
                ItemOutcome.Failed => ("FAILED ", Ui.Ansi.Red),
                ItemOutcome.Unknown => ("UNKNOWN", Ui.Ansi.Yellow),
                _ => ("SKIPPED", Ui.Ansi.Dim),
            };
            var counts = Written(result) is { } written ? $"  ({written})" : "";
            ui.Plain($"  {label}  {result.Item.Path}{counts}", style);

            if (result.Error is not null)
                ui.Plain($"           {result.Error}", Ui.Ansi.Red);
            if (result.ValidationErrors.Count > 0)
                ui.Plain($"           {result.ValidationErrors.Count} validation error(s), see log above.", Ui.Ansi.Yellow);
            if (result.UnfinishedLoad is { } loading)
            {
                var id = result.LoadTransferId is { } loadId ? $" (load ID {loadId})" : "";
                ui.Plain($"           {destination.Name} was still loading {loading}{id} when item-copy stopped, and finishes it regardless. Check the items in {destination.Name}.", Ui.Ansi.Yellow);
            }
        }

        LeftBehind(run, source, destination, ui);

        ui.Plain();
        ui.Plain("Transferred items follow your normal publishing workflow; nothing has been published.", Ui.Ansi.Dim);
    }

    /// <summary>Prints what a dry run found: each path's item count, or why the source couldn't export it.</summary>
    public static void DryRun(RunResult run, SitecoreEnvironment source, SitecoreEnvironment destination, Ui ui)
    {
        ui.Plain();
        ui.Plain($"Dry run: nothing was written to {destination.Name}.", Ui.Ansi.Bold);

        var rows = run.Items.Select(r => (Result: r, Label: r.Outcome switch
        {
            ItemOutcome.Ok => $"{r.TotalItems} item(s)",
            ItemOutcome.Failed => "FAILED",
            _ => "SKIPPED",
        })).ToList();
        var width = rows.Max(row => row.Label.Length);

        foreach (var (result, label) in rows)
        {
            ui.Plain($"  {label.PadLeft(width)}  {result.Item.Path}", result.Outcome == ItemOutcome.Failed ? Ui.Ansi.Red : null);
            if (result.Error is not null)
                ui.Plain($"  {"".PadLeft(width)}  {result.Error}", Ui.Ansi.Red);
        }

        var total = run.Items.Sum(r => r.TotalItems ?? 0);
        if (total > 0)
        {
            ui.Plain();
            ui.Plain($"Loading {total} item(s) will take {EstimateLoad(total)} (at roughly {LoadItemsPerSecond} items/second; real speed varies).", Ui.Ansi.Dim);
        }

        LeftBehind(run, source, destination, ui);
    }

    /// <summary>e.g. "312/1046 item(s) written", or null if nothing has loaded yet.</summary>
    public static string? Written(ItemResult result)
    {
        if (result.TotalItems is not { } total)
            return null;
        // The destination doesn't count items that KeepExistingItem skipped, so a low count is expected there.
        var note = result.Item.MergeStrategy == MergeStrategy.KeepExistingItem ? "; existing items kept aren't counted" : "";
        return $"{result.TransferredItems ?? 0}/{total} item(s) written{note}";
    }

    private static string EstimateLoad(int items)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(items / LoadItemsPerSecond / 60));
        return minutes < 60 ? $"about {minutes} min" : $"about {minutes / 60} h {minutes % 60} min";
    }

    private static void LeftBehind(RunResult run, SitecoreEnvironment source, SitecoreEnvironment destination, Ui ui)
    {
        var blobs = run.Items
            .SelectMany(r => r.LeftoverBlobs.Select(b => b == r.UnfinishedLoad ? $"{b} (wait for its load to finish before deleting it)" : b))
            .ToList();
        var transfers = run.Items.Where(r => r.SourceTransferExists).ToList();
        if (blobs.Count > 0 || transfers.Count > 0)
        {
            ui.Plain();
            ui.Plain("Left behind:", Ui.Ansi.Bold);
            foreach (var blob in blobs)
                ui.Plain($"  .raif blob on {destination.Name}: {blob}");
            foreach (var r in transfers)
                ui.Plain($"  transfer on {source.Name}: {r.TransferId}{(r.SourceTransferUncertain ? " (creation failed; it may not exist)" : "")}");
        }

        if (run.Cancelled)
            ui.Plain("Run was cancelled.", Ui.Ansi.Yellow);
    }
}
