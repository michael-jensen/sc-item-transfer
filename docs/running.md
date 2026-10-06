# Running item-copy

```
item-copy run <job.json> [options]

  --dry-run             Check the job, both sets of credentials, and each path's item count, then stop.
  --yes                 Skip the y/N prompt (protected destinations also need --confirm-env).
  --confirm-env <name>  Confirm a protected destination (e.g. PROD) without typing it.
  --env-file <path>     Use a specific .env file.
  --timeout <duration>  Max wait for an export or .raif, or for a load to make progress, e.g. 90s, 30m, 2h. Default 30m.
  --verbose             Log every HTTP request (tokens and secrets are never logged).
```

Run it from your working folder so it finds `.env` there (see
[Configuration](configuration.md#where-item-copy-looks-for-it)). From source, use
`dotnet run --project src/ItemCopy -- run jobs/home-to-sit.json`.

## Dry runs

Start with `--dry-run`. Besides validating the job and credentials, it exports each path from the
source to count its items, then deletes those exports. Nothing is sent to the destination:

```
Dry run: nothing was written to SIT.
  1046 item(s)  /sitecore/content/MySite/Home/Sample

Loading 1046 item(s) will take about 5 min (at roughly 4 items/second; real speed varies).
```

A count much larger than you expected means the path is too broad. A path the source can't export
fails here rather than mid-run. The load estimate helps plan a release window: loading is the slow
part, and large trees take hours.

## Confirmation

Before writing anything, item-copy prints the plan (environments, database, each path with its scope
and merge strategy, and any warnings) and asks to proceed. For a protected destination you must type
its name; `--yes` alone isn't enough there, so automation needs `--yes --confirm-env prod`.
`SITECORE_PROTECTED_ENVS` in `.env` sets which destinations are protected (default `PROD`).

## What happens

Each job item becomes its own content transfer, so items are applied **in the order listed** and
results are reported per item.

1. For each item, in order:
   1. create the transfer on the source and wait for the export to complete;
   2. stream every chunk from source to destination (4 at a time, bytes forwarded unchanged);
   3. complete the chunk set, which produces a `.raif` file in the destination;
   4. delete the transfer on the source;
   5. load the `.raif` into the destination database and wait until it finishes;
   6. delete the `.raif` file.
2. Print a summary.

If an item **fails**, the run stops, since later items may depend on it. If an item loads **with
validation errors**, the errors are printed, its `.raif` is kept in the destination for investigation,
and the run continues.

Network errors and 5xx/429 responses are retried up to 3 times. A load that fails is retried once. If
the source loses a transfer while exporting (a known Sitecore bug, CFW-9663), it is created again once.

Loading runs at roughly 4 items a second, so a large tree can take hours. item-copy waits as long as the
load keeps making progress; `--timeout` only applies once it stops.

## Cancelling

Ctrl+C cancels and cleans up source transfers; press it twice to exit immediately.

**Ctrl+C can't stop a load that has already started.** Once step 5 begins, the destination finishes
loading the `.raif` on its own. If you cancel then, item-copy warns straight away, and the summary
reports the item as `UNKNOWN` with the destination's load ID. Assume the items were written, and
check the destination.

## Reading the results

- Nothing is published.
- With `KeepExistingItem`, items that already exist are skipped and not counted as written, so
  "312/1046 item(s) written" can be a complete success.
- If a run is interrupted, the summary lists anything left behind: `.raif` files on the destination
  or transfer IDs on the source. Don't delete a `.raif` while its load may still be running.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every item copied successfully |
| 1 | Invalid job/config, authentication failure, an item failed, or cancelled |
| 2 | Every item finished, but at least one had validation errors |
