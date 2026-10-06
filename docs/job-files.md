# Job files

A job file says what to copy: from which environment, to which environment, and which items. Each run
takes one job file, so keep one per copy you make, e.g. `home-to-sit.json` or `media-to-prod.json`.

## Where to keep them

Any path works: `item-copy run <path>`. A relative path is relative to the folder you run item-copy
from, which is also where it looks for `.env` first. The [quick start](../README.md#quick-start) sets
up a working folder with a `jobs/` subfolder. A clone of this repository has the same layout, and git
ignores everything in `jobs/` except `job.example.json`, so your job files stay local.

## Example

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

## Fields

| Field | Required | Values |
|---|---|---|
| `source`, `destination` | yes | Environment names from `.env` (case-insensitive). See [Configuration](configuration.md#env). |
| `database` | no | Default `master` |
| `items[].path` | yes | Full item path starting with `/sitecore/` |
| `items[].scope` | no | `SingleItem` (default): just this item. `ItemAndDescendants`: the item and its whole subtree |
| `items[].mergeStrategy` | no | See below. Default `OverrideExistingItem` |

There's no "children only" scope in the API. To copy just the children, list each child as its own
item, or copy the parent with `ItemAndDescendants`.

## Merge strategies

A merge strategy decides what happens to items that already exist in the destination:

| Strategy | Effect |
|---|---|
| `OverrideExistingItem` | Replace the existing item with the source version. |
| `KeepExistingItem` | Leave the existing item alone; only add items that are missing. |
| `OverrideExistingTree` | **Delete** the destination item **and all its descendants**, then write the transferred items. The only strategy that deletes. With `SingleItem`, the item's existing children are deleted and not replaced. |

## Item order

Items are applied **in the order listed**, each as its own transfer, and results are reported per item.

**Parent items must already exist in the destination with the same IDs.** If they don't, the item
transfers "successfully" but doesn't appear in the content tree. Copy from a common ancestor with
`ItemAndDescendants`, or list parents before children.

item-copy checks the order before it starts:

- **error**: a path is listed before an ancestor that uses `OverrideExistingTree` (the ancestor would
  delete it);
- **warning**: a path is listed before its ancestor;
- **warning**: a path is already covered by an earlier `ItemAndDescendants` item (it is transferred
  again with its own merge strategy);
- **warning**: `OverrideExistingTree` with `SingleItem`.

## Editor support

The `"$schema"` line points your editor at [`job.schema.json`](../job.schema.json), which gives
autocomplete, descriptions and validation in VS Code and Rider. The path is relative to the job file,
so `"../job.schema.json"` works for job files in `jobs/` with the schema one level up, as in the
working folder and this repository. For a job file anywhere else, point at the published copy instead:

```json
"$schema": "https://raw.githubusercontent.com/michael-jensen/sc-item-transfer/main/job.schema.json"
```

To check a job without copying anything, run it with `--dry-run`; see
[Dry runs](running.md#dry-runs).
