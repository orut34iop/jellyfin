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

## Mandatory local workflow

- After any code change, promptly commit the finished change and push the branch.
- Every local compile or publish must include the build date/time in the version metadata. `Directory.Build.targets` generates `AssemblyInformationalVersion` automatically; for publish/install builds, use a single timestamp such as `BUILD_STAMP=$(date '+%Y%m%d%H%M%S')` and pass `/p:JellyfinBuildDateTime=$BUILD_STAMP` so all projects share the same version suffix. `AssemblyVersion` and `AssemblyFileVersion` in `SharedVersion.cs` must remain numeric.
- On macOS arm64, after any code change, rebuild the macOS arm64 app, install it to `/Applications/Jellyfin.app`, and verify it starts successfully. The minimum verification is opening `/Applications/Jellyfin.app`, confirming `http://127.0.0.1:8096/web/index.html` returns HTTP 200, and confirming `/System/Info/Public` reports the timestamped version.
- On Ubuntu or other Linux build hosts, do not require building or installing the macOS arm64 app. Build and test the relevant Linux targets with the required shared `JellyfinBuildDateTime` timestamp instead.

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

### Moonfin local file playback customization

This fork contains a Jellyfin API customization for same-host Moonfin playback of local `File` protocol media sources. The goal is to let Moonfin open the existing library symlink path directly instead of streaming or transcoding through Jellyfin when Jellyfin Server and Moonfin run on the same machine.

Relevant commits on `release-10.11.z`:

- `62f1878db3 Enable local Moonfin media paths`
- `d42b35dff8 Harden Moonfin local path playback`
- `0a0fb145a9 Skip probing Moonfin local file playback`

Relevant files:

- `Jellyfin.Api/Controllers/MediaInfoController.cs`
- `Jellyfin.Api/Helpers/MoonfinLocalPlaybackHelper.cs`
- `Jellyfin.Api/Helpers/MediaInfoHelper.cs`
- `tests/Jellyfin.Api.Tests/Helpers/MoonfinLocalPlaybackHelperTests.cs`

The special path is intentionally narrow. It applies only when all of these are true:

- The PlaybackInfo request is local (`HttpContext.IsLocal()`).
- The authenticated client name contains `Moonfin`, case-insensitive.
- The selected `MediaSourceInfo.Protocol` is `MediaProtocol.File`.
- The resolved item path exists on disk (`File.Exists(item.Path)`).

When eligible, Jellyfin writes the library item path into the existing `MediaSourceInfo.Path` field. No new API field or config option is added. For eligible Moonfin local file playback, `MediaInfoController` may call `MediaInfoHelper.GetPlaybackInfo(..., allowMediaProbe: false)` and skip POST `SetDeviceSpecificData`; Moonfin should treat `Protocol = File` plus a local absolute `Path` as directly playable and must not require populated `VideoCodec` or `AudioCodec`.

Expected Jellyfin log lines for a successful local-path response:

```text
Skipping media probe for Moonfin local file playback. ItemId: ..., MediaSourceId: ...
Using local file path for Moonfin playback. ItemId: ..., MediaSourceId: ..., Path: "/Users/wiz/media/..."
```

Expected Moonfin-side diagnostic evidence is `playMethod = directPlay` and `mediaKitOpenStart.url.isLocalFilePath = true`. If Moonfin opens `/videos/{id}/master.m3u8` or `/Videos/{id}/stream`, playback is still going through Jellyfin HTTP/HLS and this customization was not used end-to-end.

This customization does not fix general Jellyfin symlink probing or `RunTimeTicks` extraction for all clients. If media duration/probe behavior is wrong outside this same-host Moonfin local-path path, investigate the normal media probe and metadata refresh flow instead of broadening this special case.

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
