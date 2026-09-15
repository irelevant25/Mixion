---
name: mixion-add-rpc
description: Add or change a Mixion JSON-RPC method or a server-to-client notification end to end — BE handler, DI registration, state/topology mutation rules, FE IpcService call or notification subscription, store update, tests and contract docs. Use when the user asks for a new host capability the UI calls, a new push from the host to the UI, or a change to an existing RPC's params/result.
---

# Adding an RPC or a server push to Mixion

Wire protocol: JSON-RPC 2.0 text frames on `/ws` (camelCase, nulls omitted), binary frames for meters/spectrum. Requests come from the SPA; notifications (no `id`) flow either way.

## 1. Backend handler

1. Put it in the matching `BE/Mixion.Host/Ipc/Handlers/*Handlers.cs` (or a new static `XHandlers` class with a `Register(JsonRpcDispatcher dispatcher, …deps)` method).
2. Params: a `public sealed record XParams(...)` read with `JsonRpcDispatcher.RequireParams<XParams>(paramsEl)`. Reject bad input with `throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "…")`; a missing engine is `JsonRpcErrorCode.EngineUnavailable`.
3. Pick the mutation path:
   - Channel settings, routing, DSP → `engine.UpdateState(state => …)`. Validate inside the lambda (throwing publishes nothing). Put the transformation in a public static helper when it's worth testing (see `ChannelHandlers.ApplyChannelChange`).
   - Adding/removing channels or re-binding sources → `EngineHost.ChangeTopologyAsync(factory, engine => …)` (in place) or `EngineHost.RebuildAsync` (full rebuild, audible gap — explicit user actions only). Never publish a state with more channels than the engine has slots.
   - Nothing on the audio thread — handlers run on Kestrel threads.
4. Return an anonymous object or a DTO from `Ipc/StateDto.cs`. Changed channel lists return `state.ToDto()` so the FE can hydrate without a second call.
5. Register it in the dispatcher factory in `Program.cs`, resolving new dependencies from DI.

## 2. Server → client push

- `TelemetryHub.Broadcast("methodName", payload)` sends a notification to every open socket (fire-and-forget, per-connection send lock with timeout).
- Topology changes are already pushed as `stateChanged` via `EngineHost.TopologyChanged` — don't add a second push for the same thing.
- Reserved: `stateChanged`, `hostShutdown` (sent by `WebSocketEndpoint` on shutdown), `sessionChanged` (sent by `Program` after a late preset auto-load; the FE re-reads `/api/session`).

## 3. Frontend

- Calls: `ipc.call<ResultType>('methodName', params)` in a component or service; mirror BE DTO shapes as interfaces (channel/state types live in `core/mixer-state.store.ts`).
- UI-driven changes patch the store optimistically (`MixerStateStore.patchChannel` / `setRoute`) and roll back in the `catch`.
- Notifications: subscribe to `ipc.notifications$` (see the `stateChanged` subscription in `app.config.ts`) and filter by `method`.
- Components: standalone, `OnPush`, signals, separate `.ts/.html/.css`.

## 4. Tests and docs

- BE: xUnit for the pure transformation; for socket-level behaviour follow `ControlSocketTests` (real Kestrel on a random port).
- FE: a Jasmine spec next to the store/service when it has logic (`mixer-state.store.spec.ts`).
- Add the method to "RPC contract" in `BE/README.md`; update `CLAUDE.md` if you added a notification or a new mutation path.
- Verify with the `mixion-verify` skill.
