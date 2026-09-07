# Hot Reload Tool — CLI Usage Guide

Command-line interface for the Hot Reload tool for .NET Framework microservices.

## Usage

```
HotReloadTool.Host <command> [options]
```

## Commands

| Command | Status | Description |
|---------|--------|-------------|
| `start` | **Implemented** | Begin monitoring and hot-reload the worker project. |
| `stop` | **Implemented** | Stop a running hot-reload session via Named Pipes IPC. |
| `status` | **Implemented** | Print current state of a running host via Named Pipes IPC. |
| `help` | **Implemented** | Print help text and exit. |

## Options

| Option | Alias | Default | Description |
|--------|-------|---------|-------------|
| `--project <path>` | `-p` | *required* | Path to the worker `.csproj` file. |
| `--debounce <ms>` | — | `500` | Debounce interval in milliseconds. Rapid file changes within this window are coalesced into a single reload cycle. |
| `--drain-timeout <ms>` | — | `3000` | Maximum time in milliseconds to wait for connection draining before force-terminating the old worker. |
| `--pipe-name <name>` | — | `hotreload-state` | Named Pipes endpoint name for Host↔Worker IPC. |

### Additional defaults (not exposed as CLI flags)

| Setting | Default | Description |
|---------|---------|-------------|
| State transfer timeout | `5000` ms | Max wait for Named Pipes state transfer before falling back to stateless restart. |
| Retained shadow copies | `3` | Number of timestamped build copies to keep. Older ones are deleted on each successful launch. |
| Extensions | `["*.cs", "*.config"]` | File extensions that trigger a reload when changed. |
| Exclusions | `["**/obj/**", "**/bin/**", "**/*.tmp"]` | Glob patterns for files the watcher ignores. |

### Positional argument

If `--project` is not specified, the first non-flag argument is treated as the project path:

```
HotReloadTool.Host start MyService.csproj
```

This is equivalent to `--project MyService.csproj`.

## Examples

```bash
# Start with explicit flag
HotReloadTool.Host start --project MyService.csproj

# Short flag alias
HotReloadTool.Host start -p MyService.csproj

# Custom debounce and drain timeout
HotReloadTool.Host start -p MyService.csproj --debounce 300 --drain-timeout 5000

# Positional project path
HotReloadTool.Host start MyService.csproj

# Print help
HotReloadTool.Host help
```

## Stop and Status Commands

The `stop` and `status` commands communicate with a running Host via a dedicated Named Pipes endpoint (`{pipeName}-cli`).

### `stop`

Sends a graceful shutdown signal to the running Host. The Host completes any in-flight reload cycle, then exits.

```bash
HotReloadTool.Host stop
HotReloadTool.Host stop --pipe-name custom-pipe
```

- Exit code `0`: Stop signal sent successfully.
- Exit code `1`: No running host found or communication failed.

### `status`

Queries the Host for its current state and prints a summary.

```bash
HotReloadTool.Host status
HotReloadTool.Host status --pipe-name custom-pipe
```

Output:
```
Hot Reload Host Status
  Project:    C:\src\MyService\MyService.csproj
  State:      Idle
  Reloading:  False
  Worker:     running
```

Possible states: `Idle`, `Debouncing`, `Building`, `ShadowCopying`, `Draining`, `TransferringState`, `TerminatingOld`, `LaunchingNew`, `Failed`.

## Watch Mode

Use `--watch` (or `-w`) to automatically rebuild when source files change:

```bash
netman start --watch -p MyService.csproj
netman start -w MyService.csproj
```

This monitors the `src/` directory for `.cs` file changes. On change:
1. Rebuilds the solution with MSBuild
2. Restarts the host process automatically

Press Ctrl+C to stop watching.

## Behavior

### Watch directory

The watcher monitors the directory containing the `.csproj` file by default. To override, set `WatchDirectory` in the configuration object (not exposed via CLI flags).

### Shadow copies

Each successful build is copied to a timestamped temp directory before the new worker launches:

```
%TEMP%\hotreload\{ProjectHash}\{yyyyMMddHHmmss}\
```

This prevents file locks on the original build output from blocking subsequent builds.

### State logging

Every state transition in the reload orchestrator is logged to the console:

```
[14:32:07] State: Watching -> Debouncing
[14:32:07] State: Debouncing -> Building
[14:32:09] State: Building -> ShadowCopying
[14:32:09] State: ShadowCopying -> Draining
[14:32:10] State: Draining -> TransferringState
[14:32:10] State: TransferringState -> Terminating
[14:32:10] State: Terminating -> Launching
[14:32:11] State: Launching -> Watching
```

### Startup output

On successful start, the Host prints a summary before monitoring begins:

```
Hot Reload Host starting...
  Project: C:\src\MyService\MyService.csproj
  Watch:   C:\src\MyService
  Debounce: 500ms
  Drain timeout: 3000ms
  Pipe: hotreload-state
Press Ctrl+C or Enter to stop.
```

### Shutdown

Press **Ctrl+C** or **Enter** to stop the Host gracefully. The orchestrator completes any in-flight reload cycle, then exits.

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | Success, or help displayed without errors. |
| `1` | Error (e.g., missing required `--project` flag, invalid arguments, or runtime failure). |

## Non-functional requirements

- A full reload cycle **MUST** complete within 5 seconds for incremental builds.
- A build failure **MUST NOT** cause downtime — the old worker continues serving while the rebuild is retried on the next file change.
- The system **MUST** log each reload phase transition to the console.
