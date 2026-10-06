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

## Install

Each [release](https://github.com/michael-jensen/sc-item-transfer/releases) has a self-contained
executable for Linux x64 and Linux arm64 (including WSL), so .NET doesn't need to be installed. To
install, or update to the latest release:

```
arch=$(uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')
mkdir -p ~/.local/bin
curl -fsSL "https://github.com/michael-jensen/sc-item-transfer/releases/latest/download/item-copy-linux-$arch.tar.gz" | tar xz -C ~/.local/bin
item-copy --version
```

If `item-copy` isn't found, open a new terminal so `~/.local/bin` is on your `PATH`. Each release also
has a `SHA256SUMS` file for checking the downloads.

On Windows, macOS or anywhere else, [build it from source](CONTRIBUTING.md#building-an-executable).

## Quick start

### 1. Create automation credentials

For each environment you'll copy from or to, create automation credentials in the Sitecore Cloud
Portal and note the environment's host name. See [Credentials](docs/configuration.md#credentials).

### 2. Set up a working folder

item-copy reads two files: `.env`, with the credentials for each environment, and the job file you give
it. Keep them in one folder and run item-copy from there:

```
~/item-copy/
  .env                  credentials for each environment
  .env.example
  job.schema.json       lets your editor autocomplete and check job files
  jobs/
    job.example.json
    home-to-sit.json    one job file per copy you run
```

Create it from this repository's templates:

```
mkdir -p ~/item-copy/jobs && cd ~/item-copy
base=https://raw.githubusercontent.com/michael-jensen/sc-item-transfer/main
curl -fsSLo .env.example "$base/.env.example"
curl -fsSLo job.schema.json "$base/job.schema.json"
curl -fsSLo jobs/job.example.json "$base/jobs/job.example.json"
cp .env.example .env && chmod 600 .env
```

If you've cloned this repository, you can use the clone as your working folder instead. It already has
this layout, and git ignores `.env` and your job files.

### 3. Fill in `.env`

Add a host, client ID and client secret for each environment. The name in the middle (`DEV` here) is
what job files use to refer to the environment:

```
SITECORE_DEV_HOST=xmc-xxxxxxxx-dev.sitecorecloud.io
SITECORE_DEV_CLIENT_ID=...
SITECORE_DEV_CLIENT_SECRET=...
```

### 4. Write a job file

Copy the example, then set the source and destination environments and list the items to copy:

```
cp jobs/job.example.json jobs/home-to-sit.json
```

```json
{
  "$schema": "../job.schema.json",
  "source": "dev",
  "destination": "sit",
  "items": [
    { "path": "/sitecore/content/MySite/Home", "scope": "ItemAndDescendants" }
  ]
}
```

Before copying to an environment that already has content, read [Job files](docs/job-files.md). It
explains merge strategies, which decide what happens to items that already exist in the destination.

### 5. Check it, then run it

```
item-copy run jobs/home-to-sit.json --dry-run
item-copy run jobs/home-to-sit.json
```

The dry run checks the job and both sets of credentials, and counts the items each path covers,
without writing anything. The real run shows its plan and asks before writing anything. See
[Running item-copy](docs/running.md).

## Documentation

- [Configuration](docs/configuration.md): credentials, `.env`, protected environments.
- [Job files](docs/job-files.md): fields, scopes, merge strategies, item order.
- [Running item-copy](docs/running.md): options, dry runs, what happens during a run, exit codes.
- [Contributing](CONTRIBUTING.md): building, testing and the project layout.

## License

[MIT](LICENSE)
