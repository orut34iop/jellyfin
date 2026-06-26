# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project overview

This is the Jellyfin server backend: a cross-platform .NET 9 ASP.NET Core media server descended from Emby 3.5.2. It exposes a REST API for libraries, metadata, streaming, live TV, sessions, and plugins, and serves the static [jellyfin-web](https://github.com/jellyfin/jellyfin-web) client by default.

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet)
- [ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) installed and on `PATH` (or pass `--ffmpeg` at startup)
- The web client static files are **not** in this repo; obtain them from a published build, an existing install, or build them from `jellyfin-web`

## Common commands

Build everything:

```bash
dotnet build Jellyfin.sln
```

Build the server project only:

```bash
dotnet build Jellyfin.Server/Jellyfin.Server.csproj
```

Run the server (requires web client files):

```bash
dotnet run --project Jellyfin.Server --webdir /absolute/path/to/jellyfin-web/dist
```

Run the server without hosting the web client:

```bash
dotnet run --project Jellyfin.Server -- --nowebclient
```

Run the server with a custom ffmpeg path:

```bash
dotnet run --project Jellyfin.Server -- --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg
```

Run all tests:

```bash
dotnet test Jellyfin.sln --configuration Release
```

Run a single test project:

```bash
dotnet test tests/Jellyfin.Api.Tests/Jellyfin.Api.Tests.csproj
```

Run a single test by display name:

```bash
dotnet test tests/Jellyfin.Api.Tests --filter "DisplayName~MyTestMethodName"
```

Run tests with coverage (as CI does):

```bash
dotnet test Jellyfin.sln --configuration Release --collect:"XPlat Code Coverage" --settings tests/coverletArgs.runsettings --verbosity minimal
```

Restore packages:

```bash
dotnet restore
```

List outdated packages:

```bash
dotnet list package --outdated
```

### Entity Framework migrations

Migrations are provider-specific. SQLite is the only supported provider currently. From the repo root:

```bash
dotnet ef migrations add {MIGRATION_NAME} --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite" -- --migration-provider Jellyfin-SQLite
```

If `dotnet ef` is unavailable, run `dotnet tool restore` or `dotnet restore` first.

## High-level architecture

### Layer/project layout

The solution is split into three broad layers. Avoid introducing circular project references.

- **Entry / host:** `Jellyfin.Server`
  - `Program.cs`, `Startup.cs`, `StartupOptions.cs`, `CoreAppHost.cs`, migration service, API middleware registration, Kestrel configuration.
- **Abstractions / contracts:**
  - `MediaBrowser.Model` — DTOs, enums, configuration models.
  - `MediaBrowser.Common` — shared abstractions (`IApplicationPaths`, plugins, networking primitives).
  - `MediaBrowser.Controller` — domain interfaces and the `BaseItem` entity hierarchy (`Entities/`), plus manager interfaces (`Library`, `Providers`, `Session`, etc.).
- **Implementations:**
  - `Emby.Server.Implementations` — concrete managers, library scanning, scheduled tasks, sessions, HTTP server, IO, sorting, plugins.
  - `Jellyfin.Server.Implementations` — newer implementations for users, devices, activity, security, trickplay, media segments, full-system backup.
  - `src/Jellyfin.Database/Jellyfin.Database.Implementations` — EF Core `JellyfinDbContext`, entity configuration, query helpers.
  - `src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite` — SQLite-specific migrations and provider.
  - `MediaBrowser.Providers` — metadata providers for movies, music, TV, lyrics, subtitles, etc.
  - `MediaBrowser.LocalMetadata` / `MediaBrowser.XbmcMetadata` — local NFO/XML metadata parsers and savers.
  - `src/Jellyfin.LiveTv` — live TV services.
  - `src/Jellyfin.MediaEncoding.*` — encoding, HLS playlist generation, keyframe extraction.
  - `src/Jellyfin.Drawing*` — image processing (Skia-based by default).
  - `src/Jellyfin.Networking` — network and UDP discovery.

### Startup flow

1. `Jellyfin.Server.Program.Main` parses `StartupOptions`.
2. `StartupHelpers.CreateApplicationPaths` resolves data/config/cache/log/web paths (XDG on Linux, `%LocalAppData%` on Windows).
3. `ApplyStartupMigrationAsync` runs pre-initialization migrations via `JellyfinMigrationService`.
4. `StartServer` creates `CoreAppHost`, builds the generic `IHost`, configures Kestrel, and starts the web host.
5. `ApplicationHost.Init` discovers concrete types from referenced assemblies, registers core services, and lets plugins register services.
6. `ApplicationHost.RunStartupTasksAsync` initializes scheduled tasks and validates the ffmpeg path.

### Dependency injection and composition

- `ApplicationHost` is the composition root. `CoreAppHost` overrides `RegisterServices` to add Jellyfin-specific singletons.
- Plugins are discovered via `ApplicationHost.GetExports<T>` which scans `_allConcreteTypes` for assignable concrete types.
- Many managers are registered as singletons in `ApplicationHost.RegisterServices` (`ILibraryManager`, `IProviderManager`, `ISubtitleManager`, etc.).
- Controllers in `Jellyfin.Api.Controllers` are registered with `AddControllersAsServices`.

### Core domain model

- `MediaBrowser.Controller.Entities.BaseItem` is the root of every library item (folders, movies, episodes, music, photos, etc.).
- `Folder` is the base for collections and recursive scanning.
- `ILibraryManager` resolves filesystem paths into `BaseItem` instances and orchestrates library scans.
- `IProviderManager` runs metadata providers (`IMetadataProvider<T>`), local metadata parsers, and image fetchers.
- `IUserDataManager` tracks per-user playstate, favorites, and ratings.

### API layer

- `Jellyfin.Api` contains controllers. `BaseJellyfinApiController` provides common base behavior.
- Authentication uses a custom `CustomAuthenticationHandler` and a set of authorization policies (`Policies`, `Jellyfin.Api.Auth`).
- Middleware lives in `Jellyfin.Api.Middleware` and is wired in `Jellyfin.Server.Startup.Configure`.
- Swagger/ReDoc is available at `/api-docs/swagger` and `/api-docs/redoc`.
- The API supports camelCase and PascalCase JSON via custom output formatters.

### Database

- `JellyfinDbContext` is the single EF Core context. Provider selection is abstracted through `IJellyfinDatabaseProvider`.
- SQLite is the default provider. Migrations are per-provider under `src/Jellyfin.Database/Jellyfin.Database.Providers.{Provider}`.
- Repositories (`IItemRepository`, `IPeopleRepository`, `IChapterRepository`, etc.) live in `Emby.Server.Implementations/Data/`.

### Tests

- Tests use **xUnit** with **Moq** and **AutoFixture**.
- Integration tests use `Microsoft.AspNetCore.Mvc.Testing` against `Jellyfin.Server`.
- Some integration/server test projects disable parallel execution via `xunit.runner.json`.
- Coverage is collected with **coverlet** using `tests/coverletArgs.runsettings`.

## Code conventions

- Nullable reference types are enabled globally (`Directory.Build.props`).
- Warnings are treated as errors in all configurations; `NU1902` and `NU1903` are excluded.
- Debug builds enable `AnalysisMode=AllEnabledByDefault` and run analyzers:
  - StyleCop.Analyzers (`stylecop.json`)
  - IDisposableAnalyzers
  - SerilogAnalyzer
  - SmartAnalyzers.MultithreadingAnalyzer
  - Microsoft.CodeAnalysis.BannedApiAnalyzers (`BannedSymbols.txt`)
  - A custom analyzer project: `src/Jellyfin.CodeAnalysis`
- Follow `.editorconfig`: 4-space indentation for C#, 2-space for YAML/XML/CSProj, LF line endings, final newlines.
- `SharedVersion.cs` is the single source of the assembly version.

## Important file locations

- Solution: `Jellyfin.sln`
- Server entry point: `Jellyfin.Server/Program.cs`
- DI/composition root: `Emby.Server.Implementations/ApplicationHost.cs`, `Jellyfin.Server/CoreAppHost.cs`
- API startup/middleware: `Jellyfin.Server/Startup.cs`, `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`, `Jellyfin.Server/Extensions/ApiApplicationBuilderExtensions.cs`
- Base item model: `MediaBrowser.Controller/Entities/BaseItem.cs`
- Library manager implementation: `Emby.Server.Implementations/Library/LibraryManager.cs`
- Provider manager: `MediaBrowser.Providers/Manager/ProviderManager.cs`
- EF Core context: `src/Jellyfin.Database/Jellyfin.Database.Implementations/JellyfinDbContext.cs`
- Migration service: `Jellyfin.Server/Migrations/JellyfinMigrationService.cs`
- Package versions: `Directory.Packages.props`
- Build/test defaults: `Directory.Build.props`, `tests/Directory.Build.props`
