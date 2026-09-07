# IStatefulLifecycle — State Transfer Contract

Guide for worker developers who want to preserve in-memory state across hot-reload restarts.

## Overview

The Hot Reload tool transfers in-memory state from the old worker to the new worker via Named Pipes during each restart cycle. Workers optionally implement `IStatefulLifecycle` to participate. Workers that do not implement the interface restart statelessly without error.

## Interface definition

**Assembly:** `HotReloadTool.Contracts`
**Namespace:** `HotReloadTool.Contracts`

```csharp
public interface IStatefulLifecycle
{
    void Initialize(string stateJson);
    string OnShutdown();
    void BeginDrain();
    bool IsDrainComplete();
}
```

## Method contracts

### `Initialize(string stateJson)`

Called once on the new worker process after launch, before it begins accepting connections.

- `stateJson` is the JSON state snapshot captured from the previous worker's `OnShutdown()`, or `null` for a stateless or first-time start.
- **MUST** be tolerant of `null` or empty JSON.
- Use this method to restore in-memory state and reconstruct non-serializable resources (database connections, timers, file handles) from the serialized data.

### `OnShutdown()`

Called on the old worker during the drain phase.

- Must return a JSON-serializable snapshot of the worker's current in-memory state.
- **MUST complete quickly** — within the drain timeout (default 3000 ms). Do not perform long-running I/O here.
- Returns a raw JSON string (e.g., `{"Counter":42,"Sessions":["s1","s2"]}`).

### `BeginDrain()`

Signals the worker to stop accepting new connections and begin completing in-flight requests.

- After this call, the worker **SHOULD** refuse new HTTP requests and initiate WebSocket close handshakes.
- WebSocket draining **MUST NOT** cancel the `ReceiveAsync` CancellationToken until the close frame exchange completes — otherwise clients receive an abrupt disconnect.

### `IsDrainComplete()`

Polled by the Host to determine whether the worker has finished draining.

- Return `true` when all in-flight requests have completed and the worker is ready for termination.
- Return `false` if the worker is still processing in-flight requests.

## Protocol flow

```
Host                              Old Worker              New Worker
  │                                  │                       │
  │── DrainRequest ─────────────────>│                       │
  │<── DrainResponse ────────────────│                       │
  │                                  │                       │
  │── (create state transfer server) │                       │
  │<── StateTransferMessage (push) ──│                       │
  │                                  │                       │
  │── CTRL_CLOSE_EVENT ─────────────>│                       │
  │   (Kill fallback on timeout)     │                       │
  │                                  │                       │
  │── launch new worker from shadow copy ──────────────────>│
  │                                  │                       │
  │<── StateTransferMessage (pull, StateJson=null) ─────────│
  │── StateTransferMessage (stored JSON) ──────────────────>│
  │                                  │                       │
  │<── StateAck ─────────────────────│                       │
  │                                  │                       │
  │                                  │                       │── Initialize(stateJson)
```

1. Host sends `DrainRequest` via Named Pipes to the old worker.
2. Worker responds with `DrainResponse` (acknowledgment).
3. Host creates a Named Pipes server for state transfer.
4. Old worker pushes state JSON (`StateTransferMessage`) → Host stores it.
5. Host terminates old worker (`CTRL_CLOSE_EVENT` → `Kill` fallback after timeout).
6. Host launches new worker from the shadow copy build output.
7. New worker pulls state (`StateTransferMessage` with `StateJson = null` = pull request).
8. Host delivers the stored state → new worker calls `Initialize(state)`.

## JSON shape

Any valid JSON string. The Host does not inspect the contents — it is opaque payload passed between workers.

Example:

```json
{"Counter":42,"Sessions":["s1","s2"]}
```

## Serialization

- Library: **Newtonsoft.Json 9.0.1**
- State JSON is a raw string — workers serialize/deserialize their own object graphs.

## Timeout

- Default state transfer timeout: **5000 ms** (configured via `StateTransferTimeoutMilliseconds`).
- On timeout → stateless restart. The Host logs the failure and new worker receives `null` in `Initialize()`.

## Implementing the interface

Implement `IStatefulLifecycle` in the **entry point assembly** (the worker executable). The Host discovers the interface via reflection at startup.

### Minimal example

```csharp
using HotReloadTool.Contracts;
using Newtonsoft.Json;

public class StatefulWorker : IStatefulLifecycle
{
    private int _counter;
    private List<string> _sessions;

    public void Initialize(string stateJson)
    {
        if (string.IsNullOrEmpty(stateJson))
        {
            _counter = 0;
            _sessions = new List<string>();
            return;
        }

        var state = JsonConvert.DeserializeObject<StateSnapshot>(stateJson);
        _counter = state.Counter;
        _sessions = state.Sessions;
    }

    public string OnShutdown()
    {
        var state = new StateSnapshot { Counter = _counter, Sessions = _sessions.ToArray() };
        return JsonConvert.SerializeObject(state);
    }

    public void BeginDrain()
    {
        // Stop accepting new requests, begin graceful close of WebSockets
    }

    public bool IsDrainComplete()
    {
        // Return true when all in-flight requests are done
        return _inFlightCount == 0;
    }

    private class StateSnapshot
    {
        public int Counter { get; set; }
        public string[] Sessions { get; set; }
    }
}
```

## Limitations

- **Only serializable state can be transferred.** Connections, timers, file handles, and other non-serializable resources cannot cross the process boundary.
- **Non-serializable resources MUST be reconstructed** from serializable data in `Initialize()`.
- State **MUST be small enough** to serialize and deserialize within the state transfer timeout (default 5000 ms).
- The interface **MUST be implemented in the entry point assembly** (the worker executable itself, not a satellite DLL that the Host doesn't scan).
- Only **one worker process** per Host instance.

## Workers without the interface

If the worker does not implement `IStatefulLifecycle`:

- The Host detects the absence of state and **skips the state transfer phase entirely**.
- The worker restarts statelessly with **no error**.
- The reload cycle continues normally — only the in-memory state is lost.

## Out of scope

The following are explicitly **not** addressed by this tool:

- Production deployment or staging environments
- True zero-downtime hot reload (runtime agent, as in .NET 6+)
- Non-.NET Framework runtimes (CoreCLR, .NET 5+)
- IDE integration
- Memory-Mapped Files IPC
- Automatic state reconstruction for non-serializable resources
