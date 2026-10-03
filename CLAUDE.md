# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this project is

A desktop status companion for Claude Code. Claude Code hooks report session activity to a local background service; the service reduces those events into a status and pushes it to a small pixel-art character on the desktop. A Bluetooth "traffic light" hardware client is planned later and must be able to consume the same status stream.

Everything is native C# on Windows. No Docker.

## Working agreement

- The repository owner designs the architecture and writes the core framework. Implement within the existing structure. Do not add, remove, rename or merge projects, change public contracts in `Contracts`, or introduce new NuGet dependencies without asking first.
- Prefer small, reviewable changes. When a task is ambiguous, ask one focused question instead of guessing.
- The owner is learning C# and knows Rust well. When introducing a C# concept that has a close Rust analogue (channels, async tasks, ownership of disposables, traits vs interfaces), a one-line comparison in the explanation is welcome. Keep code comments factual and short.
- Reply to the owner in Chinese. Code, identifiers, commit messages and comments are in English.

## Repository layout

```
assets/                Pixel art assets (read-only, see "Assets")
plugin/                Claude Code plugin that registers the hooks
src/
  StatusHub.Contracts/ Shared DTOs: hook event payloads, status enum, status snapshot
  StatusHub.Service/   Windows Service: receives hooks, reduces state, broadcasts
  DeskPet.App/         WPF app: transparent always-on-top window that renders the character
tests/                 Test projects mirroring src/
```

If the actual tree differs from this, trust the tree and tell the owner.

## Architecture

1. Collection: the plugin registers hooks for `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Notification`, `Stop` and `StopFailure`. Each hook forwards its JSON payload to the service over HTTP on localhost. Hook commands must be fast and must never block or fail Claude Code: short timeout, swallow errors, exit 0.
2. Reduction: the service pushes incoming events into a `System.Threading.Channels` channel. A single `BackgroundService` reads it and owns all mutable state, so no locks are needed. State is tracked per `session_id`; stale sessions are expired by timeout. When several sessions are active, the displayed status is chosen by a fixed priority.
3. Distribution: two kinds of output.
   - Status (latest value wins): the current aggregated snapshot, held as an immutable record and replaced atomically. New clients receive it immediately on connect.
   - Events (fire once): one-shot moments such as "task finished" or "error", broadcast to connected clients.
   Clients connect through a SignalR hub. The WPF app must tolerate the service starting before or after it and reconnect automatically.

Statuses: `idle`, `thinking`, `working`, `waiting` (needs user input or permission), `done` (one-shot, returns to idle), `error` (one-shot, returns to idle).

## Assets

`assets/` is the asset directory and the root of the extracted `pixel-girl` package. Treat it as read-only input; never edit, re-encode or move files there.

- `assets/runtime/sprites/*.png`: layer images, all on the same 119x129 canvas, top-left aligned
- `assets/runtime/manifest.json`: layer order and animation parameters (frame durations, blink timing, talk toggle). This file is the source of truth; never hard-code frame names or timings in C#.
- `assets/runtime/pixel-idle.gif`: reference of the finished idle animation
- `assets/preview/`, `assets/pipeline/`, `assets/source_images/`: tooling and AI source images; not used at runtime and not shipped

Rendering rules for the character:
- Composite layers bottom to top in manifest order: back hair frame, main body, eye patch, mouth patch. Patches contain only changed pixels; everything else is transparent.
- Scale only by integer factors with nearest-neighbor sampling (`RenderOptions.BitmapScalingMode="NearestNeighbor"`, no layout rounding blur, snap the window to whole device pixels). Never smooth, filter or fractionally scale sprites.
- New states (thinking, working, transitions) will be added as new layers plus manifest entries. Design the animation player around the manifest so adding a state needs no code change beyond mapping status to animation.

## Environment notes

- Development happens on a Windows machine that also runs WSL. Claude Code itself may run inside WSL. In that case the hook commands run in Linux and must reach the service on Windows: rely on WSL mirrored networking (`networkingMode=mirrored` in `.wslconfig`) so `localhost` works, and keep the port configurable.
- The WPF project targets Windows. When building from WSL or Linux, `EnableWindowsTargeting` must be set so restore and build succeed; running it requires Windows.
- The service listens on localhost only. Never bind to external interfaces.

## Commands

```
dotnet build
dotnet test
dotnet run --project src/StatusHub.Service
dotnet run --project src/DeskPet.App      # Windows only
```

Run `dotnet build` and `dotnet test` before reporting a task as done.

## C# conventions

- Nullable reference types enabled, warnings as errors, file-scoped namespaces, implicit usings.
- Shared settings live in `Directory.Build.props`; do not repeat them in individual project files.
- Async all the way; pass `CancellationToken` through every async API and honor it on shutdown.
- Use `record` types for DTOs and snapshots. Use the generic host (`Host.CreateApplicationBuilder`), dependency injection and `IOptions<T>` for configuration.
- Log through `ILogger<T>`; no `Console.WriteLine` in the service.
