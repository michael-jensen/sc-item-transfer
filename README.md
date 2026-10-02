# item-copy

Copy Sitecore content between SitecoreAI environments (e.g. DEV → SIT → PROD) from the command
line, using Sitecore's [Content Transfer API](https://api-docs.sitecore.com/sai/content-transfer-api)
and [Item Transfer API](https://api-docs.sitecore.com/sai/item-transfer-api). It replaces
Package Designer–style content packages.

You describe what to copy in a small JSON job file, then run one command:

```
item-copy run jobs/home-to-sit.json
```

It exports the items from the source, streams them to the destination, loads them into the
destination database, and cleans up after itself. Transferred items are **not published**; they follow
your normal publishing workflow.

## Setup

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build (not needed to run a
published executable, see [Publishing](#publishing)).

### 1. Create automation credentials

You need the **Organization Admin** or **Organization Owner** role. For **each** environment:

1. Sitecore Cloud Portal → SitecoreAI Deploy → **Credentials** → **Environment** →
   **Create credentials** → **Automation**.
2. Copy the client ID and secret (they can't be viewed again).
3. Note the host name: **Projects** → your project → **Authoring environments** → your environment →
   **Details** → **Environment host name**.

### 2. Create `.env`

```
cp .env.example .env
```

Fill in a host, client ID, and client secret per environment. The name in the middle is what job
files refer to, so any names work, not just DEV/SIT/PROD:

```
SITECORE_DEV_HOST=xmc-xxxxxxxx-dev.sitecorecloud.io
SITECORE_DEV_CLIENT_ID=...
SITECORE_DEV_CLIENT_SECRET=...
```

`.env` is gitignored. item-copy looks for it in the current directory, then next to the executable;
`--env-file` overrides both. Real environment variables take precedence over `.env` (useful in CI).

`SITECORE_PROTECTED_ENVS` (default `PROD`) lists destinations that require typing the environment
name to confirm.

### 3. Create a job file

```
cp jobs/job.example.json jobs/home-to-sit.json
```

Everything in `jobs/` except `*.example.json` is gitignored, so job files stay local.

```json
{
  "$schema": "../job.schema.json",
  "source": "dev",
  "destination": "sit",
  "database": "master",
  "items": [
    { "path": "/sitecore/content/MySite/Home", "scope": "ItemAndDescendants", "mergeStrategy": "OverrideExistingItem" },
    { "path": "/sitecore/media library/Project/MySite", "scope": "ItemAndDescendants", "mergeStrategy": "KeepExistingItem" },
    { "path": "/sitecore/content/MySite/Data/Footer" }
  ]
}
```

The `$schema` line gives autocomplete and validation in VS Code and Rider.

| Field | Required | Values |
|---|---|---|
| `source`, `destination` | yes | Environment names from `.env` (case-insensitive) |
| `database` | no | Default `master` |
| `items[].path` | yes | Full item path starting with `/sitecore/` |
| `items[].scope` | no | `SingleItem` (default): just this item. `ItemAndDescendants`: the item and its whole subtree |
| `items[].mergeStrategy` | no | See below. Default `OverrideExistingItem` |

**Merge strategies**, for items that already exist in the destination:

| Strategy | Effect |
|---|---|
| `OverrideExistingItem` | Replace the existing item with the source version. |
| `KeepExistingItem` | Leave the existing item alone; only add items that are missing. |
| `OverrideExistingTree` | **Delete** the destination item **and all its descendants**, then write the transferred items. The only strategy that deletes. With `SingleItem`, the item's existing children are deleted and not replaced. |

There's no "children only" scope in the API. To copy just the children, list each child as its own
item, or copy the parent with `ItemAndDescendants`.

## Running

```
item-copy run <job.json> [options]

  --dry-run             Validate the job and test credentials for both environments, then stop.
  --yes                 Skip the y/N prompt (protected destinations also need --confirm-env).
  --confirm-env <name>  Confirm a protected destination (e.g. PROD) without typing it.
  --env-file <path>     Use a specific .env file.
  --timeout <duration>  Max time for any single wait, e.g. 90s, 30m, 2h. Default 30m.
  --verbose             Log every HTTP request (tokens and secrets are never logged).
```

From source, use `dotnet run --project src/ItemCopy -- run jobs/home-to-sit.json`.

Start with `--dry-run`, then try a single `SingleItem` job before larger ones.

Before writing anything, item-copy prints the plan (environments, database, each path with its scope
and merge strategy, and any warnings) and asks to proceed. For a protected destination you must type
its name; `--yes` alone isn't enough there, so automation needs `--yes --confirm-env prod`.

### What happens

Each job item becomes its own content transfer, so items are applied **in the order listed** and
results are reported per item.

1. All transfers are created on the source at once and prepared in parallel.
2. For each item, in order:
   1. wait for the source export to complete;
   2. stream every chunk from source to destination (4 at a time, bytes forwarded unchanged);
   3. complete the chunk set, which produces a `.raif` file in the destination;
   4. delete the transfer on the source;
   5. load the `.raif` into the destination database and wait until it finishes;
   6. delete the `.raif` file.
3. Print a summary.

If an item **fails**, the run stops, since later items may depend on it. Transfers already created for
the remaining items are deleted. If an item loads **with validation errors**, the errors are printed,
its `.raif` is kept in the destination for investigation, and the run continues.

Network errors and 5xx/429 responses are retried up to 3 times. A load that fails is retried once.
Ctrl+C cancels and cleans up source transfers; press it twice to exit immediately.

**Ctrl+C can't stop a load that has already started.** Once step 5 begins, the destination finishes
loading the `.raif` on its own. If you cancel then, item-copy warns straight away, and the summary
reports the item as `UNKNOWN` with the destination's load ID. Assume the items were written, and
check the destination.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Every item copied successfully |
| 1 | Invalid job/config, authentication failure, an item failed, or cancelled |
| 2 | Every item finished, but at least one had validation errors |

## Things to know

- **Parent items must already exist in the destination with the same IDs.** If they don't, the item
  transfers "successfully" but doesn't appear in the content tree. Copy from a common ancestor with
  `ItemAndDescendants`, or list parents before children.
- item-copy checks ordering before it starts:
  - **error**: a path is listed before an ancestor that uses `OverrideExistingTree` (the ancestor would
    delete it);
  - **warning**: a path is listed before its ancestor;
  - **warning**: a path is already covered by an earlier `ItemAndDescendants` item (it is transferred
    again with its own merge strategy);
  - **warning**: `OverrideExistingTree` with `SingleItem`.
- Nothing is published.
- If a run is interrupted, the summary lists anything left behind: `.raif` files on the destination
  or transfer IDs on the source. Don't delete a `.raif` while its load may still be running.

## Publishing

Build a single self-contained executable that teammates can run without installing .NET:

```
dotnet publish src/ItemCopy -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/ItemCopy -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64
dotnet publish src/ItemCopy -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
```

Put `.env` next to the executable, or run it from a directory containing `.env`.

## Development

```
dotnet build
dotnet test
```

```
src/ItemCopy/
  Program.cs            entry point: load config, validate, plan, confirm, run
  Cli/                  options, confirmation, console output, plan/summary
  Config/               .env parsing, named environments, job model and validation
  Http/                 tokens, authorised requests with refresh and retry
  Api/                  Content Transfer and Item Transfer API clients
  Pipeline/             run orchestration and results
tests/ItemCopy.Tests/   unit tests, plus pipeline tests against an in-memory fake of both APIs
docs/superpowers/specs/ design spec
```

The pipeline tests use `tests/ItemCopy.Tests/Fakes/FakeSitecore.cs`, which emulates auth, the source,
and the destination based on the published OpenAPI specs. It hasn't been verified against every
real-world response, so the first live runs should be small.
