# item-copy — SitecoreAI content copy CLI

**Date:** 2026-10-01
**Status:** Approved, implemented

## Purpose

A small .NET console app that copies Sitecore content between SitecoreAI
environments (DEV, SIT, PROD) using the Content Transfer API and the Item
Transfer API. A user describes a copy job in a local JSON file (paths, scope,
merge strategy), runs one command, and the content lands in the destination
database.

Job files are local working files and are not committed; a committed example
file is the template. Environment hosts and automation-client secrets live in a
gitignored `.env`.

### Success criteria

- One command (`item-copy run jobs/<job>.json`) performs the full migration:
  source export → chunk copy → `.raif` generation → load into destination DB →
  cleanup.
- Paths are applied to the destination in exactly the order written in the job.
- Results are reported per path (item counts, validation errors, outcome).
- Nothing is written to a destination without explicit confirmation; protected
  environments (PROD by default) require typing the environment name.
- No secrets in Git; no secrets or tokens printed to the console.

### Out of scope

- A "children only" scope. The API supports only `SingleItem` and
  `ItemAndDescendants`; children are copied by listing them as separate paths
  or by copying the parent with `ItemAndDescendants`.
- Uploading/consuming local `.raif` files (`fileName` / upload endpoints).
- Publishing. Transferred items follow normal publishing workflows.
- Persisted run state / resume across process restarts.

## API facts this design relies on

Sources: Content Transfer API v1.0 and Item Transfer API 3.0 OpenAPI specs
(`api-docs.sitecore.com/_bundle/sai/{content-transfer-api,item-transfer-api}/index.yaml`).

**Auth.** `POST https://auth.sitecorecloud.io/oauth/token`, form-encoded,
`grant_type=client_credentials`, `client_id`, `client_secret`,
`audience=https://api.sitecorecloud.io`. JWT lasts 24h. Each environment has its
own automation client. Unexpected `401`/`403` → request a new token.

**Content Transfer API** — base `https://{host}`, prefix
`/sitecore/api/content/transfer/v1/transfers`:

| Step | Env | Call | Success |
|---|---|---|---|
| Create | source | `POST /transfers` body `{TransferId, Configuration:{DataTrees:[{ItemPath, Scope, MergeStrategy}], Database}}` | 202 |
| Status | source | `GET /transfers/{transferId}/status` → `{State: Running\|Completed\|Failed\|NotFound, ChunkSetsMetadata:[{ChunkSetId, ChunkCount, TotalItemCount}]}` | 200 |
| Get chunk | source | `GET /transfers/{id}/chunksets/{chunksetId}/chunks/{chunkId}` → binary; `Content-Disposition` params `IsMedia`, `ItemsProcessed`, `ItemsSkipped` | 200 |
| Save chunk | destination | `PUT` same path `?isMedia={bool}`, `Content-Type: application/octet-stream`, body = bytes exactly as received | 201 |
| Complete set | destination | `POST /transfers/{id}/chunksets/{chunksetId}/complete` → `{ContentTransferFileName}` | 200 |
| Delete | source | `DELETE /transfers/{id}` | 202 |

`Scope` ∈ `SingleItem` (default), `ItemAndDescendants`.
`MergeStrategy` ∈ `OverrideExistingItem` (default), `KeepExistingItem`,
`OverrideExistingTree` (deletes the destination item and its descendants first;
the only strategy that deletes).

**Item Transfer API** — destination only, base
`https://{host}/sitecore/shell/api/v3/ItemsTransfer`:

| Step | Call | Notes |
|---|---|---|
| Blob state | `GET /sources/blobs/{blobName}` → `{BlobState, Error, SourceName}` | ready when `Uploaded` |
| Consume | `POST /transfers/databases/{db}/sources?blobName={name}` | 202, `Location` header |
| Status | `GET /transfers/{transferId}` → `{Id, SourceName, TransferState, TotalItemsCount, TransferredItemsCount, ValidationErrors, ...}` | done when `Finished` |
| List | `GET /transfers?page=&pageSize=` → `{Transfers:[TransferStatusResult]}` | fallback lookup |
| Retry | `PUT /transfers/databases/{db}/sources/{sourceName}` | only when `Failed` |
| Delete blob | `DELETE /sources/blobs/{blobName}` | 204 |

`TransferState` ∈ `Unknown, InProgress, Finished, Failed, Queued, Discarded`.
`BlobState` ∈ `Unknown, Uploading, Uploaded, Initializing, Error, Consumed,
Transferred, TransferredWithErrors, Queued, Discarded`.

**Known doc inconsistency.** The walkthrough says the last segment of the
consume `Location` header is the transfer ID; the endpoint reference says it is
the blob name. See "Resolving the item-transfer ID" below.

**Parent chain.** An item's parent chain must already exist in the destination
with the same IDs, or the item transfers but does not appear in the tree.

**Chunk sets carry no path.** Status metadata has no item path, so with several
DataTrees in one transfer the `.raif`→path mapping and ordering are unknown.
This drives the one-transfer-per-path decision.

**Field reports (community blogs, not official docs).** Loads write roughly 4
items/second (1,718 items ≈ 7 min; 74,060 items took hours). Known bug
CFW-9663: status can return "not found" about 3–5 minutes after a transfer is
created; whether the transfer comes back is not stated.

## Configuration

### `.env`

```
SITECORE_DEV_HOST=xmc-xxxx-dev.sitecorecloud.io
SITECORE_DEV_CLIENT_ID=...
SITECORE_DEV_CLIENT_SECRET=...
SITECORE_SIT_HOST=...
SITECORE_SIT_CLIENT_ID=...
SITECORE_SIT_CLIENT_SECRET=...
SITECORE_PROD_HOST=...
SITECORE_PROD_CLIENT_ID=...
SITECORE_PROD_CLIENT_SECRET=...

# Optional
SITECORE_PROTECTED_ENVS=PROD
SITECORE_AUTH_URL=https://auth.sitecorecloud.io/oauth/token
SITECORE_AUTH_AUDIENCE=https://api.sitecorecloud.io
```

- An environment named `x` (case-insensitive) is configured when
  `SITECORE_{X}_HOST`, `_CLIENT_ID`, `_CLIENT_SECRET` are all non-empty. Names
  match `[A-Za-z0-9_]+`. Nothing is hard-coded to DEV/SIT/PROD.
- `HOST` may be given with or without `https://`; a trailing `/` is trimmed.
- Lookup: `--env-file <path>` if given; else `./.env`; else `.env` next to the
  executable. A missing file is fine if real environment variables are set.
- Real process environment variables override `.env` values.
- `.env` syntax: `KEY=VALUE`, blank lines, `#` comments, optional `export `
  prefix, optional single or double quotes around the value. No interpolation.
- `SITECORE_PROTECTED_ENVS`: comma-separated; default `PROD`. Empty string
  disables protection.

### Job file

```jsonc
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

- `database` optional, default `master`. Used for both the source transfer
  configuration and the destination consume call.
- `scope` optional, default `SingleItem`; `mergeStrategy` optional, default
  `OverrideExistingItem`. Enum values case-insensitive; sent to the API in
  canonical casing.
- JSON comments and trailing commas are allowed.
- `job.schema.json` at repo root provides editor autocomplete/validation.

### Validation (before any API call)

Errors (abort, exit 1):
- `source`/`destination` missing, not configured in env, or equal.
- `items` missing or empty.
- `path` empty or not starting with `/sitecore/` (case-insensitive).
- Duplicate paths (case-insensitive, trailing `/` ignored).
- Unknown `scope` / `mergeStrategy` value.
- A descendant path is listed **before** an ancestor that uses
  `OverrideExistingTree` (the ancestor would delete it).

Warnings (shown in the plan, run continues):
- A descendant is listed before its ancestor (parent may not exist yet in the
  destination when the descendant loads).
- A path sits under an earlier `ItemAndDescendants` path (redundant; it will be
  re-applied with its own merge strategy).
- `OverrideExistingTree` with `SingleItem` (deletes the item's existing
  descendants in the destination without replacing them).

"Ancestor" = path prefix on a `/` boundary, case-insensitive.

### Repository files

- `.gitignore`: `.env`, `jobs/*` except `jobs/*.example.json`, `bin/`, `obj/`,
  `publish/`.
- Committed: `.env.example`, `jobs/job.example.json`, `job.schema.json`,
  `README.md`.

## Command line

```
item-copy run <job.json> [--yes] [--confirm-env <name>] [--dry-run]
                         [--env-file <path>] [--timeout <duration>] [--verbose]
item-copy --help | --version
```

- `--timeout`: max wait for a source export or a blob to be ready, and the
  longest an item load may go without progress. Accepts `90s`, `30m`, `2h`.
  Default `30m`.
- `--verbose`: logs each HTTP method, URL, status, and duration. Never logs the
  `Authorization` header, secrets, or tokens.

### Confirmation flow

1. Load env + job, validate, print the plan: source name→host, destination
   name→host, database, then a numbered table of paths with scope and merge
   strategy. `OverrideExistingTree` rows are highlighted as destructive.
   Warnings follow the table.
2. `--dry-run`: acquire a token for source and destination (verifies
   credentials), then create a source transfer per item (all concurrently),
   wait for each export, print each path's item count (or why it failed) and an
   estimated load time for the total at 4 items/second, and delete the
   transfers. Nothing is sent to the destination. Exit 0 if every path
   exported, else 1.
3. Destination not protected: prompt `Proceed? [y/N]` unless `--yes`.
4. Destination protected: prompt `Type the destination environment name (PROD)
   to continue:`; must match case-insensitively. `--yes` alone does **not**
   skip this; `--yes --confirm-env prod` does.
5. If confirmation is required and stdin is not interactive → abort, exit 1.

## Pipeline

Each job item becomes its own content transfer with its own new GUID.

Get tokens for both environments first (fail fast on bad credentials). Then,
per item, in job order:

1. *Create:* create the item's source transfer. It is created only when its
   turn comes, because loads are slow (see field reports) and a transfer created
   up front could wait on the source for hours. A create rejected with a 4xx is
   known not to exist and is not cleaned up; a network error or 5xx may have
   created it, so cleanup tries to delete it and the summary marks it as
   possibly non-existent.
2. *Wait for export:* poll source status every 5s until `Completed`. `Failed` →
   item fails. `NotFound` is tolerated for 1 minute after creation; if it lasts
   longer (CFW-9663), the transfer is created again with the same ID (which
   overwrites it) and given another minute. `NotFound` after that → item fails.
   Timeout → item fails.
3. *Copy chunks:* for each chunk set (normally one), copy chunks `0..ChunkCount-1`
   with up to 4 concurrent copies. A copy = source GET (response headers read,
   body streamed) → destination PUT streaming the same body with
   `?isMedia={IsMedia}`. `IsMedia` missing from `Content-Disposition` → item
   fails (never guessed). `ItemsSkipped > 0` → warning with count.
4. *Complete:* once all chunks of a set are saved, POST complete; record
   `ContentTransferFileName`.
5. *Delete source transfer* (always attempted once chunks are downloaded or the
   item has failed; failures here are warnings).
6. *Wait for blob:* poll `GET /sources/blobs/{name}` every 5s until `Uploaded`.
   `Error`/`Discarded` → item fails.
7. *Consume:* POST consume with `blobName`; resolve the transfer ID (below).
8. *Monitor:* poll `GET /transfers/{transferId}` every 5s. The timeout restarts
   whenever `TransferredItemsCount` changes, so a large load that keeps writing
   items is waited for however long it takes; it times out only after
   `--timeout` with no progress.
   - `Finished`: read blob state. If `ValidationErrors` is non-empty or blob is
     `TransferredWithErrors` → item **partial**: print errors, keep blob.
     Otherwise item **ok**: delete blob.
   - `Failed`: call retry once, continue polling. Second `Failed` → item fails;
     blob kept.
   - `Discarded` → item fails.
9. Print item result: path, items transferred/total, outcome. With
   `KeepExistingItem` the count notes that kept items aren't counted (the API
   excludes items skipped by the merge strategy).

**Failure policy.** A **failed** item stops the run: later items may depend on
it, and their source transfers are never created. A **partial** item does not
stop the run.

**Ctrl+C.** Cancels in-flight work, then best-effort deletes all source
transfers this run created. Blobs already generated are listed in the summary.
A load the destination has already started can't be cancelled: the destination
finishes it. An item cancelled mid-load is reported as **unknown** (not
skipped), with its load ID, and a warning is printed as soon as Ctrl+C is pressed.

**Summary.** One row per item: outcome (ok / partial / failed / skipped / unknown), item
counts, `.raif` name if left behind, and any source transfer IDs that could not
be deleted.

**Exit codes.** `0` all ok. `1` validation/config/auth error, any failed item,
or cancelled. `2` every item finished but at least one was partial.

### Resolving the item-transfer ID

Let `last` = final path segment of the consume `Location` header (URL-decoded).

1. `GET /transfers/{last}` → 200: use `last`.
2. Otherwise page `GET /transfers` (pageSize 50) for entries with
   `SourceName == blobName` (case-insensitive); choose the newest `ConsumedDate`;
   use its `Id` for `GET /transfers/{Id}`.
3. Steps 1–2 retried every 5s for up to 1 minute (the transfer may not be
   listed yet). Still not found → item fails with a message naming the blob.

### HTTP behaviour

- All requests: `Authorization: Bearer`, `Accept: application/json`.
- **Token refresh:** on `401`/`403`, invalidate the cached token for that
  environment, fetch a new one, and repeat the request once.
- **Transient retry:** `408`, `429`, `5xx`, network errors, and timeouts are
  retried up to 3 times with 2s/4s/8s backoff, honouring `Retry-After` on 429.
  Applies to GETs, DELETEs, the create POST (same `TransferId` is an
  overwrite, so it's safe), and chunk copies. A chunk copy is retried as a
  unit (fresh GET + PUT) because the streamed body cannot be replayed.
- **Not retried** (non-idempotent): complete-chunk-set POST, consume POST,
  retry PUT.
- Non-success responses surface the API's `{"Error": "..."}` message when
  present.

## Code structure

```
ItemCopy.slnx
src/ItemCopy/                      net10.0 console, AssemblyName=item-copy, no NuGet deps
  Program.cs                       arg parsing, command dispatch, exit codes
  Cli/Options.cs                   parsed options + duration parsing
  Cli/Console.cs                   output helpers (colour, prompts, tables)
  Config/EnvFile.cs                .env parser
  Config/EnvironmentRegistry.cs    named-environment resolution, protected envs
  Config/Job.cs                    job model, JSON loading, enums
  Config/JobValidator.cs           validation rules → errors + warnings
  Http/TokenProvider.cs            client-credentials token per environment
  Http/SitecoreHttp.cs             send helper: auth header, refresh, retry, errors
  Api/ContentTransferClient.cs     Content Transfer endpoints
  Api/ItemTransferClient.cs        Item Transfer endpoints + ID resolution
  Api/ContentDisposition.cs        IsMedia / ItemsProcessed / ItemsSkipped parsing
  Api/Models.cs                    request/response DTOs
  Pipeline/TransferRunner.cs       per-item steps, failure policy, cleanup
  Pipeline/RunResult.cs            per-item results, summary, exit code
tests/ItemCopy.Tests/              xUnit
```

Each API client takes an `HttpClient` and a `TokenProvider`, so tests inject a
fake `HttpMessageHandler`. Polling interval and clock are injectable so tests run
instantly.

## Testing

Unit tests:
- `EnvFile`: comments, quotes, `export`, blank lines, `=` in values, process
  env override.
- `EnvironmentRegistry`: case-insensitive names, missing keys, host
  normalisation, protected-env parsing (default, custom, empty).
- `Job` + `JobValidator`: defaults, enum casing, every error and warning rule.
- `ContentDisposition`: parameter casing, quoted values, missing `IsMedia`.
- Duration parsing.

Pipeline tests (fake handler emulating source, destination, and auth):
- Happy path, 2 items, multi-chunk: order of calls, `isMedia` forwarded, bytes
  forwarded unchanged, source transfers and blobs deleted.
- Chunk GET 503 then success → retried.
- Token 401 mid-run → refreshed once.
- Transfer ID fallback: `GET /transfers/{last}` 404 → found via list.
- Item `Failed` → retry → `Finished`.
- `ValidationErrors` → partial, blob kept, exit code 2.
- Item 1 fails → item 2 not consumed and its source transfer never created, exit 1.
- Item 2's source transfer is created only after item 1 has loaded.
- Source loses a transfer (`NotFound` past the grace period) → created again
  once; lost again → item fails.
- Load slower than `--timeout` but making progress → ok; no progress → times out.

Live verification against real SitecoreAI is manual (no credentials in this
environment): `--dry-run` first, then a single `SingleItem` job DEV→SIT.

## Distribution

- Dev: `dotnet run --project src/ItemCopy -- run jobs/my-job.json`
- Publish single file:
  `dotnet publish src/ItemCopy -c Release -r <win-x64|osx-arm64|linux-x64>
  --self-contained -p:PublishSingleFile=true -o publish/<rid>`
- README documents setup (automation clients, `.env`, job file), commands,
  the parent-chain caveat, and merge-strategy semantics.
