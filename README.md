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
