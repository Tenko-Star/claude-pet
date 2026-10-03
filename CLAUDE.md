# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this project is

A desktop status companion for Claude Code. Claude Code hooks report session activity to a local background service; the service reduces those events into a status and pushes it to a small pixel-art character on the desktop. A Bluetooth "traffic light" hardware client is planned later and must be able to consume the same status stream.

Everything is native C# on Windows. No Docker.

## Working agreement

- The repository owner designs the architecture and writes the core framework. Implement within the existing structure. Do not add, remove, rename or merge projects, change public contracts in `Contracts`, or introduce new NuGet dependencies without asking first.
- Prefer small, reviewable changes. When a task is ambiguous, ask one focused question instead of guessing; this does not replace the mandatory understanding confirmation (see "Development rules").
- The owner is learning C# and knows Rust well. When introducing a C# concept that has a close Rust analogue (channels, async tasks, ownership of disposables, traits vs interfaces), a one-line comparison in the explanation is welcome. Keep code comments factual and short.
- Language: implementation plans are written in English. All other communication with the owner (questions, understanding confirmations, explanations, reports) is in Simplified Chinese. Code, identifiers and code comments are in English. Commit message summaries are in Simplified Chinese (see "Commit messages").

## Repository layout

```
assets/                Pixel art assets (read-only, see "Assets")
installer/             Inno Setup installer script and its Chinese language file
plugin/                Claude Code plugin that registers the hooks
scripts/               Service install/uninstall scripts and the installer build script
src/
  StatusHub.Contracts/ Shared DTOs: hook event payloads, status enum, status snapshot
  StatusHub.Service/   Windows Service: receives hooks, reduces state, broadcasts
  DeskPet.App/         WPF app: transparent always-on-top window that renders the character
    BuiltInCharacters/ Extra files of built-in character packages (character.json)
tests/                 Test projects mirroring src/
```

If the actual tree differs from this, trust the tree and tell the owner.

## Architecture

1. Collection: the plugin registers hooks for `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `SubagentStop`, `Notification`, `Stop` and `StopFailure`. Each hook forwards its JSON payload to the service over HTTP on localhost. Hook commands must be fast and must never block or fail Claude Code: short timeout, swallow errors, exit 0.
2. Reduction: the service pushes incoming events into a `System.Threading.Channels` channel. A single `BackgroundService` reads it and owns all mutable state, so no locks are needed. State is tracked per `session_id`; stale sessions are expired by timeout. When several sessions are active, the displayed status is chosen by a fixed priority.
3. Distribution: two kinds of output.
   - Status (latest value wins): the current aggregated snapshot, held as an immutable record and replaced atomically. New clients receive it immediately on connect.
   - Events (fire once): one-shot moments such as "task finished" or "error", broadcast to connected clients.
   Clients connect through a SignalR hub. The WPF app must tolerate the service starting before or after it and reconnect automatically.

Statuses: `idle`, `thinking`, `working`, `waiting` (needs user input or permission), `done` (one-shot, returns to idle), `error` (one-shot, returns to idle), `toolFailure` (one-shot, status unchanged).

- `thinking` only covers "prompt received, no tool called yet". From the first `PreToolUse` of a turn the session stays `working` until `Stop`, `StopFailure` or `Notification`, because Claude Code fires no hook while the model writes the next tool call.
- `working` carries a pace in the snapshot: `active` (a tool is executing, after `PreToolUse`) or `composing` (between tools, after `PostToolUse`, `PostToolUseFailure` or `SubagentStop`). Across sessions the pace is `active` if any working session is active.
- A composing session with no new `PreToolUse` for `Status:ThinkFallback` (default 45 s) falls back to `thinking`; the service checks every second.

## Assets

`assets/` is the asset directory. Treat it as read-only input; never edit, re-encode or move files there. The only exception is a change to `assets/runtime/manifest.json` or `assets/preview/*.html` that the owner explicitly asks for; sprite images are never touched.

`assets/runtime/` is the built-in character `claude-girl`: the DeskPet build copies it to `characters\claude-girl\` next to the executable, together with `src/DeskPet.App/BuiltInCharacters/claude-girl/character.json` (display name). User characters live in `%LOCALAPPDATA%\ClaudePet\characters\<id>\` and override a built-in one with the same id.

- `assets/runtime/sprites/*.png`: character layer images on the same 119x129 canvas, top-left aligned, plus `fx_*.png` effect sprites
- `assets/runtime/manifest.json`: stage size and character offset, layer order, and animation parameters (frame durations, blink timing, states with enter/exit frames, effects, tap rhythm and its paces, reactions). This file is the source of truth; never hard-code frame names or timings in C#.
- `assets/runtime/pixel-idle.gif`: reference of the finished idle animation
- `assets/preview/`: HTML demos; `state-demo.html` embeds a copy of `manifest.json`, keep the two in sync
- `assets/pipeline/`, `assets/source_images/`: tooling and AI source images; not used at runtime and not shipped

Rendering rules for the character:
- Composite layers bottom to top in the manifest `layers` order (hair, body, eyes, mouth, fx) onto the manifest stage; character layers sit at `characterOffset`, effect sprites are placed in stage coordinates. Patches contain only changed pixels; everything else is transparent.
- Scale only by integer factors with nearest-neighbor sampling (`RenderOptions.BitmapScalingMode="NearestNeighbor"`, no layout rounding blur, snap the window to whole device pixels). Never smooth, filter or fractionally scale sprites.
- The animation player is driven by the manifest: adding or retuning a state needs no code change beyond mapping status to a state name in `PetController`.

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

These are for reference. The owner runs builds manually. Do not run `dotnet build`, `dotnet test` or other checks proactively; see "Tests, checks, lints and builds".

## C# conventions

- Nullable reference types enabled, warnings as errors, file-scoped namespaces, implicit usings.
- Shared settings live in `Directory.Build.props`; do not repeat them in individual project files.
- Async all the way; pass `CancellationToken` through every async API and honor it on shutdown.
- Use `record` types for DTOs and snapshots. Use the generic host (`Host.CreateApplicationBuilder`), dependency injection and `IOptions<T>` for configuration.
- Log through `ILogger<T>`; no `Console.WriteLine` in the service.

## Development rules

These rules cover behavioral boundaries, collaboration workflow, exploration, tools, Git and implementation discipline. They were merged from the owner's user-level rules; where they conflicted with earlier project rules, these rules won.

### Scope and precedence

These rules deliberately override Claude Code defaults in three places:

1. **Sub-agent use.** This file is a standing instruction to use the `logic-explorer` sub-agent; it satisfies any default requiring the user to ask first.
2. **Understanding confirmation.** An explicit confirmation round is required before any code-modifying task, overriding the default of acting once enough information is available.
3. **Automatic commit.** A commit is required after every approved file-changing task, overriding the default of committing only on request.

**No ceremony.** Do not build a state machine, classification framework, or ritual around these rules. Apply the relevant one directly. The goal is correctness, predictability, and efficient collaboration — not process for its own sake.

### Language

Implementation plans are written strictly in English. Everything else communicated to the owner is in **Simplified Chinese**: requirement clarification, blocking questions, understanding confirmation, requests to approve extra scope, Git dirty-worktree questions, database clarification, failure-recovery authorization requests, explanations, and completion reports.

Code, identifiers and code comments stay in English. Commit message summaries are in Simplified Chinese.

Do not reveal private chain-of-thought. Give concise conclusions, evidence, decisions, questions, and results.

### Requirement boundary

The owner's explicit requirements are the complete task boundary. Honor restrictive wording — only, just, do not, no need, minimal, keep, preserve, do not change, do not add — and never silently broaden it.

#### Unapproved expansion is forbidden

Without explicit approval, never independently introduce:

- new product behavior, new features, or additional user-visible states;
- new abstractions, generic frameworks, factories, or registries;
- compatibility layers or fallback systems;
- dependencies or configuration systems;
- unrelated refactors, opportunistic cleanup, or broad API migrations;
- performance optimizations outside the approved target;
- fixes for unrelated existing defects;
- speculative future-proofing.

This is a hard boundary. If one of these looks useful, necessary, safer, cleaner, or architecturally preferable, do **not** include it automatically. Instead: investigate enough to explain why it may be needed, raise it before creating the plan, state the smallest relevant tradeoff in Simplified Chinese, ask whether to include it, and add it only after explicit approval.

#### No expansion during execution

Once a plan is approved or execution has started, do not add unapproved scope. If new repository facts show that additional scope is materially required: stop before implementing it, explain the newly discovered fact in Simplified Chinese, state what appears necessary, ask whether to extend scope, and wait for approval. Never reinterpret an approved plan as permission for adjacent work.

If new facts invalidate the approved approach entirely, do not force the plan through — stop at the point it becomes invalid and request the necessary decision.

#### Minimal-scope implementation

Use the smallest implementation that satisfies the approved requirement. Prefer existing local patterns, utilities, components, framework conventions, and naming; keep changes cohesive and local.

Avoid touching distant modules unless the approved behavior genuinely requires it. Do not perform global replacement merely because a new API was introduced, do not migrate unrelated callers, and do not clean up nearby code unless that cleanup was explicitly approved.

#### Pre-existing code issues

If investigation reveals a problem outside the approved scope — an unrelated bug, fragile code, inconsistent naming, obsolete code, an optimization or abstraction opportunity, an unneeded compatibility issue, a missing cleanup, a refactor opportunity — do not fix it automatically.

Before plan creation: report it concisely in Simplified Chinese, explain why it matters to the current task (if it does), state the smallest recommended action, say whether the task can continue without it, and ask whether to include it. During execution, if such an issue newly appears and fixing it requires expanding scope, stop and ask first.

### Exploration

When a task requires understanding existing project logic, prefer the `logic-explorer` sub-agent over broad exploration by the main agent — for locating entry points, finding files or symbols, understanding a feature, tracing callers and callees, following control/data/state flow, understanding routing, service-layer, persistence, event handlers, callbacks, middleware, DI or factory wiring, message producers/consumers, or configuration wiring, identifying side effects, locating non-obvious behavior before a modification, investigating a bug, or preparing context for a refactor.

Give the explorer the owner's actual question, known relevant symbols/routes/files/modules/table names, confirmed task boundaries, and important non-goals.

The main agent stays responsible for interpreting intent, judging remaining ambiguity, presenting the understanding confirmation, requesting scope decisions, producing the plan, modifying files, managing Git, and reporting failures and completion.

**Avoid duplicate exploration.** After the explorer covers an area, do not repeat the same broad search in the main agent — start from its reported locations and line ranges. Small direct inspections are fine when the exact location is already known, one narrow factual check is needed, a direct check would sharpen the next explorer task, or a material explorer conclusion needs verification. If another substantial branch must be traced, delegate that branch back to the explorer.

#### Explorer memory

The explorer maintains memories under `~/.claude/memories/explorer/<project-folder-name>/` and may consult them before searching. Encourage memory use where it reduces repeated discovery, and encourage writing memory when an investigation uncovers reusable non-obvious knowledge: entry points, non-obvious call chains, stable business flows, major data flows, persistence paths, important side effects, framework wiring, routing and message-flow relationships, or anything that took substantial work to find.

**Memory is a stale cache, never a source of truth.** The current repository is always authoritative. Prefer memory to guide navigation and reuse remembered entry points as hints, but re-verify every material conclusion against the current repository and update stale memory when that has future value. Never silently upgrade a memory-derived assumption into a verified fact.

#### Explorer retry

An incomplete, truncated, or timed-out explorer invocation is **not** an exploration failure and does not trigger the hard-stop policy. Do not abandon the investigation, do not treat it as evidence the logic cannot be found, and do not immediately fall back to broad main-agent exploration. Retry with the same objective and boundary.

If the exploration is too large for one invocation, decompose the same objective into smaller focused tasks — entry point and routing; service-layer flow; persistence flow; event or message flow; one caller branch; one data-flow branch. Decomposition must not broaden the task.

**Cap: at most three retry or decomposition rounds per objective.** If it is still unresolved, stop, report in Simplified Chinese what was and was not established, and ask how to proceed. Do not retry indefinitely.

Genuine operational failures — explorer unavailable, invalid configuration, denied permissions, malformed invocation — use normal failure handling instead.

### Understanding confirmation

#### Investigate first

Before presenting the confirmation, do enough read-only investigation that it reflects the actual repository rather than paraphrasing the prompt. Permitted: `logic-explorer`; reading, searching, and listing project files; Git status and history; configuration; symbol and reference lookup; reading existing tests without generating or running them; database structure inspection where available and relevant.

Do not modify project files during this phase. If a fact can be determined from the repository, configuration, explorer, or database, investigate it instead of asking the owner to repeat what is already available.

#### Confirmation is mandatory

Every task that will modify code must receive an understanding confirmation before planning or implementation, in both plan mode and non-plan mode. There is no "direct edit" exception — "直接改", "不用问了直接改", "按上面的内容改", or "just change it" does not bypass it.

Its purpose is to let the owner quickly detect a misunderstanding of the requested behavior, the target scope, preservation requirements, explicit exclusions, or material ambiguity. It is **not** an implementation plan.

**Format:** Simplified Chinese, short, normally 2–5 points. Include only what helps the owner spot a misunderstanding — the requested behavior; the files, feature areas, or system boundaries at a high level; behavior that must stay unchanged; explicit non-goals; unresolved blocking ambiguity. No implementation steps, no exact code changes (unless the code change *is* the requirement), no preliminary plan.

```markdown
## 理解确认

1. ...
2. ...
3. ...

如果以上理解正确，我再继续生成计划或执行修改。
```

Include one blocking question concisely if one remains. Wait for explicit confirmation before planning or modifying code.

#### Clarification and optional scope

Ask only questions that materially affect the result; never ceremonial questions whose answers the project already settles. Ask when product behavior is genuinely unspecified, two plausible readings produce materially different results, requirements conflict, a destructive choice is unauthorized, an important external fact cannot be inspected, extra scope appears necessary or desirable, or another rule here requires the owner's decision.

If exploration reveals an out-of-scope improvement: do not implement it, do not silently add it to the plan — raise it before plan creation, explain why it matters, ask whether to include it. If declined, exclude it completely.

### Implementation plans

Generate a plan only after the understanding confirmation is explicitly confirmed. **Plans are written strictly in English.**

**Self-contained.** Assume the owner may clear context before executing the plan. Never rely on "as discussed above", "as mentioned earlier", "the previously described approach", or "the earlier decision" without restating the decision in the plan.

**Required context:** objective; confirmed requirements and non-goals; current relevant behavior; important existing architecture and control-flow facts; relevant files, modules, routes, services, repositories, components, and configuration; important caller/callee relationships; database and schema facts when relevant; owner decisions made during clarification; behavior that must remain unchanged; compatibility expectations; exact implementation scope; ordered implementation steps; expected Git behavior; any authorized testing or validation, including what the owner will validate manually; and known constraints or risks.

Keep it to executable context — no background essays or generic best-practice material.

**Only approved work.** A plan must not contain an unapproved feature, abstraction, dependency, compatibility layer, broad refactor, optimization, migration, API redesign, or cleanup task. If one is necessary, get approval before generating the plan.

### Git worktree preflight

Before a task that will modify files, inspect the worktree. If it already contains uncommitted changes, stop before modifying anything and ask the owner, in Simplified Chinese, to choose:

1. stash the existing changes;
2. create a separate worktree for the task;
3. ignore the existing changes and continue in the current worktree.

Do not choose on the owner's behalf.

**Ignore semantics.** If the owner chooses ignore: continue in the current worktree and stop treating it as a blocker; do not ask about the same pre-existing changes again; never clean, reset, restore, stash, or revert unrelated changes; modify only files the approved task requires; leave unrelated modified files alone for the rest of the task; do not try to make the repository clean before completion. If a task-touched file already contained owner changes, do not separate or rewrite them unless the owner asks for hunk-level isolation — at commit time, commit task-touched files as whole files.

**Stash / worktree.** Perform only the selected workflow; never silently switch. If that Git operation fails, apply the hard-stop policy — it changes repository state.

### Editing discipline

#### Context before editing

Understand enough of the existing implementation to make a correct narrow change: affected files, callers and callees, types, data structures, state flow, API contracts, routing, persistence, configuration, framework wiring, existing local patterns, similar implementations. Prefer `logic-explorer` for substantial exploration.

Never implement from a guessed architecture. Never add a helper, abstraction, dependency, or pattern before checking whether the project already has an appropriate mechanism.

#### Compatibility and versioned change

Prefer preserving existing external behavior.

**Small changes** that do not require changing the original function, method, route, or public signature: keep the existing contract, modify the existing block narrowly, preserve unrelated external behavior, and do not introduce a parallel API unnecessarily.

**Large or breaking changes:** prefer a versioned parallel path over replacing the original globally. Follow C# and project conventions — `FooV2`, a versioned service method, route, or request/response type, or another project-native mechanism; overloading is fine where conventions match.

The intent is a branch in behavior: preserve the original implementation and keep its external behavior working; mark the old path deprecated (for example `[Obsolete]`) or scheduled for removal where that fits project conventions; update only the call chain the approved task requires; leave unrelated callers on the original version. Do not globally replace every old call unless the owner explicitly approved a full migration. If introducing a versioned path is itself outside approved scope, request approval before putting it in the plan.

#### Formatting

Avoid formatting operations as much as possible. Do not establish a formatter baseline at task start, do not run a formatter (including `dotnet format`) after completion, do not format project-wide merely because a formatter exists, do not create or change formatter configuration, and do not install or upgrade formatting tools.

When editing: preserve the local style of the touched code, make the smallest syntactically correct edit, and avoid unrelated whitespace churn, line-ending churn, and reformatting of surrounding code.

Format only when the owner explicitly asks, or when the change cannot be completed correctly without it. If formatting would materially expand the diff, ask first.

### Tools and commands

**Shell.** Use the Bash tool (Git Bash / POSIX sh). Do not reach for PowerShell.

**Preference hierarchy** when several options do the same job:

1. the dedicated tool that performs the operation directly — `Read`, `Grep`, `Glob`, `Edit`, `Write` for files and search; LSP/IDE tools for symbol-level questions. These are read-only-safe and never prompt, so they are the default for reading, searching, and listing;
2. a command already covered by `permissions.allow` in `~/.claude/settings.json`;
3. another simple project-native command when materially better suited;
4. a small helper script only when genuine processing or control flow is needed;
5. a command requiring approval when nothing above can reasonably do the job.

This is a preference hierarchy, not a prohibition list — a command is not forbidden merely because it is not allowlisted; use normal approval. Equally, do not run an allowlisted command mechanically just because it is allowed.

**Standard commands before workarounds.** Prefer direct conventional commands. Do not replace a working CLI or dedicated tool with a custom script, elaborate shell construction, indirect file parsing, or repeated ad-hoc transformations. With search commands such as `rg`: keep invocations simple, prefer fixed-string search when regex is unnecessary, avoid clever patterns, and do not escalate complexity after an error. Prefer `git grep` when it clearly does the same job; `rg` is fine when materially better (untracked files, glob filtering).

**Never work around permissions.** Do not wrap a command in another shell to bypass approval, split one restricted operation into several calls to evade checks, write a script solely to bypass approval, or disguise a state-changing operation as an inspection command.

Command permission and task authorization are separate. An allowlisted command does not grant permission to skip understanding confirmation, bypass the worktree preflight, expand scope, modify files before authorization, add dependencies, generate tests, make unapproved database changes, or ignore any other rule here.

**Helper scripts.** Do not write one when a command or dedicated tool does the job clearly. When genuinely justified: prefer Python; keep it narrowly scoped; put throwaway scripts in the session scratchpad and scripts worth keeping under the project's `.claude/scripts/`; avoid complex inline `python -c` when quoting or structured data is involved; retain useful scripts for auditability. A helper script is not a project feature unless the owner asked for one, and is not staged or committed unless it is an intentional task output or the owner wants it committed.

### Database

Safety and authorization are enforced by the configured MCP/database permission system — do not recreate restrictions here.

When database access is available and relevant, proactively tell the owner the capability exists, then decide autonomously whether to use it; do not require the owner to request inspection first. Inspection may cover databases, schemas, tables, columns, indexes, constraints, table definitions, permitted data reads, and other metadata.

**Inspect structure before important SQL.** Check the current schema or table definition before running SQL that depends on it. Current database structure outranks old assumptions.

**One SQL statement per execution.** Each database execution call must contain exactly one statement. Never bundle (`UPDATE ...; INSERT ...;`) — submit separate executions, even when the tool supports multiple statements.

### Tests, checks, lints and builds

**Tests require explicit permission.** Minimize test-code generation by default and never create tests proactively. Before generating or modifying test code, or executing tests (`dotnet test`), stop and get explicit permission unless it was already clearly given for the current task. If testing would materially help, explain why in Simplified Chinese and ask. General implementation authorization is not test authorization, and tests do not go into a plan unless approved. If the owner declines, proceed without them.

**Checks, lints, compilation, builds.** Do not proactively recommend or execute check, lint, compile, build, format-check, or broad static-analysis commands — the default expectation is that the owner runs these manually. Run one only when the owner explicitly asks, subject to normal command permissions. Being allowlisted is not a reason to run something automatically; this applies to `dotnet build`, `dotnet format --verify-no-changes`, analyzers, and equivalents.

### Failure handling

#### Harmless read-only recovery

Do not hard-stop for every harmless command mistake. A read-only inspection command may be corrected and retried automatically when the failure is clearly command construction with no meaningful side effect — a wrong flag, malformed search expression, quoting or regex mistake, a safely correctable project-relative path, a glob mistake, or a usage error the output explains.

Identify the construction problem, simplify where possible, prefer a more direct invocation over added complexity, and retry without asking for authorization. **At most two consecutive automatic corrections per logical operation** — then stop and report rather than cycling through elaborate alternatives.

#### No-result handling

A documented "no result" is not automatically a failure. Distinguish command execution failure from valid execution with no matches. A valid no-match may be followed by a safe refinement — check spelling, search a related symbol, widen the project-relative scope, or try another read-only search tool. Never invoke the hard-stop policy merely because nothing was found, and never treat "not found" as proof the behavior cannot exist elsewhere unless the searched scope justifies it.

#### Hard-stop boundary

Hard-stop when a failure may affect repository state, data, permissions, or safety, or when the resulting state is uncertain: permission denial; sandbox or ACL failure; a state-changing Git command failure; a partially successful file write; an edit whose resulting file state is uncertain; a database write or migration failure; a dependency-changing operation failure; anything that may have produced partial side effects; an unexpected state where continuing could overwrite owner work; or inability to tell whether a state-changing operation completed.

When one occurs: stop the affected task immediately; do not silently retry; do not switch to a different state-changing tool; do not compensate with another write; do not clean up uncertain side effects without authorization.

Report in Simplified Chinese, in this order:

**Problem:** what failed, what is confirmed, what is uncertain, and any known or possible partial side effect.

**Original command/invocation:** the exact failed invocation, secrets redacted with explicit placeholders.

**Proposed corrected command/invocation:** the smallest correction the evidence supports — or an explicit statement that no correction can be proposed from the available evidence.

Then ask the owner to authorize the corrected invocation, an unchanged retry, or another clearly described recovery action, and wait. Permission to retry one invocation does not authorize a materially different state-changing operation.

#### Unexpected results

A command can succeed while producing an unexpected result. Classify by practical impact:

- **Harmless discovery mismatch** — no matches, a wrong path assumption, a read-only query returning less than expected. Use safe discovery recovery above.
- **Real code or data problem** — the implementation behaves differently from the owner's assumption, a requested target does not exist, or a requested validation exposes a real defect. Stop before silently changing scope; explain the new fact and return to clarification when necessary.
- **Uncertain or stateful mismatch** — the wrong file was modified, an expected edit did not occur, partial state may have been written, or Git state is not what the operation should have produced. Apply the hard-stop boundary.

### Completion and commit

**Untracked artifacts.** Do not avoid an appropriate authorized command merely because it may create ordinary local artifacts. After the work is done, inspect Git status before committing. If new untracked files appeared that are not intentional task outputs — caches, generated temporaries, unignored `bin/`/`obj/` output, accidental helper outputs — do not delete, stage, or commit them; tell the owner they exist and should be removed if unneeded.

**Automatic commit.** Every task that actually modifies files creates a Git commit once the approved work is complete, unless the owner explicitly says otherwise. Do not ask for a second commit confirmation after the task was already authorized. This does not override the worktree preflight.

**Commit scope.** Stage and commit only files actually touched for the approved task. Never stage unrelated modified files, unrelated pre-existing changes, unrelated untracked files, caches, build artifacts, or non-output helper files. Do not modify, reset, restore, clean, or stash other dirty files to make the repository clean. A failed commit is a state-changing Git failure — apply the hard-stop boundary.

**Checklist.** After an approved file-changing task: no broad formatting; no proactive test generation; no proactive tests, checks, lints, compilation, or builds; inspect Git status enough to identify task files and unexpected artifacts; warn about unrelated new untracked artifacts without deleting them; stage only task-touched files; create the commit unless told otherwise; leave everything unrelated untouched.

Keep the completion response concise — normally the commit hash, the commit message, and any warning still needing attention.

#### Commit messages

```text
<type>: <concise summary>
```

Types: `feat`, `fix`, `deps`, `opt`, `others`. The summary is in Simplified Chinese.

For multiple distinct approved changes:

```text
type: 简洁中文摘要

1. 第一项变更
2. 第二项变更
```

The message describes only the current task — never unrelated pre-existing changes in other files. A session trailer appended automatically by the harness is not part of this convention.

### Backend

Do not assume modification permission from discussion, investigation, review, explanation, or planning alone — understanding confirmation plus explicit owner confirmation is required before modification.

When the work depends on existing control flow, persistence, API routing, background processing, queue/channel logic, or shared services, prefer `logic-explorer`.

Do not generate backend tests unless explicitly authorized, and do not proactively run tests, checks, lints, builds, or compilation.
