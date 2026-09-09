# Folder Assistant

A local assistant that indexes a folder on your machine and answers questions about what
is in it. Everything runs in one process, against one folder, with no service to deploy.

## Status

**The write side works end to end; the read side is built but not yet connected.**

What runs today: the application indexes a folder — scanning it, chunking the text, embedding
the chunks and storing the vectors in a folder-scoped SQLite database — off the startup path,
and keeps the index current from a filesystem watcher. Cosine retrieval over those vectors is
implemented and tested, with two interchangeable embedding implementations behind one seam.

What does not run yet: **nothing calls retrieval at runtime.** There is no agent, no tools and
no chat surface, so the vectors are written and never read outside the test suite. That is the
next substantial piece of work, not an oversight —
[docs/DOCUMENTATION-ADJUSTMENTS-2026-09-09.md](docs/DOCUMENTATION-ADJUSTMENTS-2026-09-09.md)
records it, and [docs/diagrams/implementation-status.md](docs/diagrams/implementation-status.md)
shows what is live, what is built but unreachable, and what is not built.

## Requirements

- .NET 10 SDK

## Build and test

```bash
dotnet build
dotnet test
```

## Configure

Defaults work with no configuration: the folder the app is started in is the folder it indexes.

To point it somewhere else, or to change indexing behaviour, edit
`FolderAssistant/appsettings.json` — or use environment variables or user secrets, which is what
you want for anything you would not commit:

```json
{
  "FolderAssistant": {
    "Persistence": { "AnalyzedFolderPath": "C:/some/folder" },
    "Indexing": { "Enabled": true, "WatchEnabled": true }
  }
}
```

For local development, copy the template and fill in your own values:

```bash
cp FolderAssistant/appsettings.Development.template.json FolderAssistant/appsettings.Development.json
```

`appsettings.Development.json` is **git-ignored**, and it takes precedence over
`appsettings.json` whenever the app runs in the Development environment.

## Run

```bash
dotnet run --project FolderAssistant
```

**This writes to the folder it is pointed at.** It creates a `.folderassistant/` directory
holding a SQLite database, indexes every text file it finds, and then keeps watching the folder
and re-indexing when anything changes — until you stop it. With no configuration that folder is
the working directory, so run it from somewhere you meant.

The database is derived state: deleting `.folderassistant/` and running again rebuilds it.

Set `FolderAssistant:Indexing:Enabled` to `false` to start the host without any of that.

## Layout

```
FolderAssistant/            the application
  Indexing/                 scan, chunk, watch, and the readiness gate
  Embedding/                the vectorizer seam and its two implementations
  Persistence/              SQLite bootstrap, connections, and the stores
  Retrieval/                cosine search over the stored vectors
FolderAssistant.Tests/      tests for both halves
docs/                       specifications, diagrams and archived designs
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
- [`docs/diagrams/`](docs/diagrams/) — the architecture as it is meant to hold together, and
  [implementation-status.md](docs/diagrams/implementation-status.md) for how much of it exists.
- [`docs/archive/`](docs/archive/) — designs that were considered and not built, kept for
  the reasoning that rejected them.

Start at [SPEC-000](docs/specs/SPEC-000-system-concept.md). The storage choice and its
alternatives are in [SPEC-131](docs/specs/SPEC-131-database-options-analysis.md).
