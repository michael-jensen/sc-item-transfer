# Contributing

## Building and testing

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
```

Run from source with `dotnet run --project src/ItemCopy -- run jobs/home-to-sit.json`. Builds made this
way report version `0.0.0-dev`; only release builds carry a real version.

The pipeline tests use `tests/ItemCopy.Tests/Fakes/FakeSitecore.cs`, which emulates auth, the source,
and the destination based on the published OpenAPI specs. It hasn't been verified against every
real-world response, so the first live runs should be small.

## Project layout

```
src/ItemCopy/
  Program.cs            entry point: load config, validate, plan, confirm, run
  Cli/                  options, confirmation, console output, plan/summary
  Config/               .env parsing, named environments, job model and validation
  Http/                 tokens, authorised requests with refresh and retry
  Api/                  Content Transfer and Item Transfer API clients
  Pipeline/             run orchestration and results
tests/ItemCopy.Tests/   unit tests, plus pipeline tests against an in-memory fake of both APIs
docs/                   user documentation
docs/superpowers/specs/ design spec
```

## Pull requests

`main` is protected, so every change goes through a pull request. A pull request can merge once the
CI check (`Build and test`) passes and the branch is up to date with `main`.

## Building an executable

Releases include Linux x64 and arm64 executables. For any other platform, build a single
self-contained executable that runs without .NET installed, using its
[runtime identifier](https://learn.microsoft.com/dotnet/core/rid-catalog):

```
dotnet publish src/ItemCopy -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/ItemCopy -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64
```

Then run it from a working folder with a `.env`, as described in the
[quick start](README.md#quick-start).

## Releasing

Releases are made by the maintainer; see [RELEASING.md](RELEASING.md).
