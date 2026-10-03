# plugin

Claude Code plugin that registers the hooks feeding StatusHub.

It will register hooks for `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Notification`, `Stop` and `StopFailure`. Each hook forwards its JSON payload to `StatusHub.Service` over HTTP on localhost (port configurable). Hook commands must be fast and must never block or fail Claude Code: short timeout, swallow errors, exit 0.

Placeholder only; no hook scripts yet.
