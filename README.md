# Folder Assistant

A local assistant that indexes a folder on your machine and answers questions about what
is in it. Everything runs in one process, against one folder, with no service to deploy.

## Status

Early. This repository currently holds the solution skeleton: project layout, centralised
package management, analyzer configuration and editor settings. The application itself is
built up from here.

## Requirements

- .NET 10 SDK

## Build and test

```bash
dotnet build
dotnet test
```

## Run

```bash
dotnet run --project FolderAssistant
```

## Layout

```
FolderAssistant/            the application
FolderAssistant.Tests/      its tests
Directory.Packages.props    centrally managed package versions
```

Package versions are managed centrally, so `.csproj` files reference packages without
versions. Add a new package's version to `Directory.Packages.props`.

## Conventions

- Tabs for indentation.
- BCL type names spelled out — `String`, `Int32`, `Boolean` — rather than the C# keywords.
- `this.` on instance member access.
- Nullable reference types enabled.

The analyzer runs on every build. Warnings are not noise to be scrolled past; a build that
reports more than it did yesterday has introduced something.

## Documentation

- [`docs/specs/`](docs/specs/) — one specification per module. Most are placeholders for
  now and say so; they exist as a set so a module being built has somewhere to record its
  decisions at the time they are taken, rather than somewhere to write them up afterwards.
- [`docs/diagrams/`](docs/diagrams/) — the architecture as it is meant to hold together.
- [`docs/archive/`](docs/archive/) — designs that were considered and not built, kept for
  the reasoning that rejected them.

Start at [SPEC-000](docs/specs/SPEC-000-system-concept.md). The storage choice and its
alternatives are in [SPEC-131](docs/specs/SPEC-131-database-options-analysis.md).
