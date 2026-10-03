# plugin

Claude Code plugin (`deskpet-hooks`) that forwards hook events to `StatusHub.Service`.

It registers command hooks for `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Notification`, `Stop` and `StopFailure`. Each hook runs `scripts/forward-hook.sh <EventName>`, which POSTs the hook JSON from stdin to `http://127.0.0.1:<port>/hooks/<EventName>`.

The script never gets in Claude Code's way: curl has a 0.2 s connect timeout and a 1 s total limit, all output is discarded, and the script always exits 0. If the service is not running, the event is simply dropped.

```
plugin/
  .claude-plugin/
    plugin.json        plugin manifest (name, userConfig "port")
    marketplace.json   single-plugin local marketplace, so the plugin can be installed persistently
  hooks/hooks.json     hook registrations
  scripts/forward-hook.sh
```

## Requirements

- `sh` and `curl` on the PATH of the environment where Claude Code runs. Linux/WSL distributions normally have both; on native Windows, Claude Code runs hooks through Git Bash, which provides `sh`, and Windows ships `curl.exe`.
- Claude Code in WSL with the service on Windows: WSL mirrored networking (`networkingMode=mirrored` under `[wsl2]` in `%UserProfile%\.wslconfig`). With mirrored networking, `127.0.0.1` inside WSL reaches the Windows loopback, where the service listens.

## Install

Paths below are from the repository root. In WSL, the repository is under `/mnt/<drive>/...`, e.g. `/mnt/e/Code/claude-pet/plugin`.

### Option A: one session only (for trying it out)

```sh
claude --plugin-dir ./plugin
```

The plugin is loaded for that session only and nothing is installed.

### Option B: persistent install from the local marketplace

```sh
claude plugin marketplace add ./plugin
claude plugin install deskpet-hooks@deskpet-local
```

The plugin is loaded in place from this directory, so edits to the scripts or `hooks.json` take effect on the next session start or after `/reload-plugins`. To remove it:

```sh
claude plugin uninstall deskpet-hooks@deskpet-local
claude plugin marketplace remove deskpet-local
```

Check the manifests after editing them:

```sh
claude plugin validate ./plugin
claude plugin validate ./plugin/.claude-plugin/plugin.json
```

### Port

The service port defaults to `47821` (`HookIngest:Port` in `src/StatusHub.Service/appsettings.json`). If you change it, set the plugin's `port` option to the same value in `/config`. The hook script reads it from `CLAUDE_PLUGIN_OPTION_PORT` and falls back to `47821`.

## Verify that events arrive

1. Start the service (on Windows):

   ```sh
   dotnet run --project src/StatusHub.Service
   ```

   The log should show `Now listening on: http://127.0.0.1:47821`.

2. Optionally, test the forwarding path without Claude Code, from the same environment Claude Code runs in (WSL or Windows):

   ```sh
   echo '{"session_id":"manual-test"}' | sh plugin/scripts/forward-hook.sh SessionStart
   ```

3. Start Claude Code with the plugin, send a prompt, and let it use a tool.

4. Look at the log. With the default `HookIngest:DataDirectory` (empty), it is:

   ```
   %LOCALAPPDATA%\ClaudePet\hooks\hook-events.jsonl
   ```

   In PowerShell: `Get-Content "$env:LOCALAPPDATA\ClaudePet\hooks\hook-events.jsonl" -Wait -Tail 20`.

   Each line is one request:

   ```json
   {"receivedAt":"2026-10-03T07:26:39.74+00:00","eventName":"UserPromptSubmit","payload":{"session_id":"...","hook_event_name":"UserPromptSubmit","prompt":"..."}}
   ```

   A body that is not valid JSON is kept verbatim as `"rawBody"` instead of `"payload"`.

If nothing shows up:

- Check that the service is reachable from where Claude Code runs: `curl -i -X POST http://127.0.0.1:47821/hooks/test -d '{}'` should return `HTTP/1.1 204 No Content`. From WSL, a failure here usually means mirrored networking is not enabled (restart WSL with `wsl --shutdown` after editing `.wslconfig`).
- Check that the plugin is loaded: `claude plugin list`.
- Start Claude Code with `--debug`; the debug log under `~/.claude/debug/` records hook execution.
