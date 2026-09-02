# Authentication Refresh E2E Plan

## Phase 1: Driver Controls And Automatic Refresh

- Parameterize `.NET` matrix cases with auto-refresh, reconnect, refresh lead time, and bounded run duration.
- Add a noninteractive TypeScript automatic-refresh mode with expected callback count and timeout.
- Add opt-in `-IncludeAutoRefresh` cases for WS/SSE/LP in both clients.
- Assert stable connection id, successful callback count, refreshed marker/service token, and no reconnect.

## Phase 2: Deadline, Concurrency, Identity, And Policy

- Expose `MaximumAuthenticationExpiration` in the Default sample configuration.
- Add cap-with-later-ticket and cap-with-no-ticket-expiration cases.
- Add close-on-expiration-disabled token-lifetime/deadline checks.
- Add queued parallel lifetime refreshes and verify the final observed deadline is not below the
  maximum accepted expiration.
- Capture complete snapshots before and after blocked-role and different-user refreshes; assert no
  principal, callback-count, or deadline mutation.

## Phase 3: Protocol, Errors, And Compatibility

- Correct Default and Serverless negotiate-v0 expectations to connection-not-found.
- Extend endpoint probes only where the current host/runtime exposes a deterministic status.
- Automate the old SDK host: lifetime omitted, no automatic refresh request, stable live connection.
- Keep malformed protocol payload and internal/cross-pod failures in focused SDK/runtime tests rather
  than fabricating them at the sample boundary.

## Phase 4: Optional Long-Running Continuity

- Add `-IncludeLongRunning` cases with short but safe token lifetimes.
- Auto-refresh control: at least two cycles, stable id, monotonic ticks, no loss/reconnect.
- No-refresh control: close-on-expiration, reconnect, new id, and observable tick gap.
- Keep these opt-in so the deterministic core matrix remains suitable for normal iteration.

## Phase 5: Optional OSS Runtime + Local Upstream Propagation

- Build the Function host with `EnableUpstreamFunctions=true` and verify all three upstream
  functions are indexed.
- Use the local OSS Service Runtime endpoint on port 8888 for both Management SDK and SignalR bindings.
- Invoke and broadcast before and after `/refresh`; assert changed upstream marker claims, the raw
  claims header, broadcast payloads, successful refresh, and stable connection id.
- When a second endpoint is supplied, configure both under `Azure:SignalR:Endpoints`, repeat the case
  with `Endpoint1` and `Endpoint2` explicitly selected during negotiate, and assert each pass
  negotiated to its expected owning endpoint. Keep refresh and claims operations on the full context
  so owner lookup remains part of the test.

## Validation

1. Build Default host, .NET client, compatibility host, Function host, and TypeScript client.
2. Parse `run-refresh-auth-e2e-matrix.ps1` with the PowerShell parser.
3. Run synthetic parser/assertion tests for new log shapes.
4. Run the Default core matrix.
5. Run opt-in auto-refresh/long-running cases when the local runtime is reachable.
6. Run Serverless Transient/Persistent only when their configured resources/transports are available.

## Requirement Mapping

| Requirement | Planned Evidence |
| --- | --- |
| Automatic refresh E2E | `default-auto-dotnet-{ws,sse,lp}`, `default-auto-typescript-{ws,sse,lp}` |
| Deadline boundaries | maximum cap, no-ticket cap, close-disabled, stale/max deadline cases |
| Concurrency strengthening | parallel-10 and mixed-lifetime refresh cases |
| Identity and policy | accepted role change plus blocked-role and different-user unchanged-state cases |
| Protocol and errors | v0 rejection and deterministic endpoint/auth error cases |
| Long-running and compatibility | old-SDK case and opt-in tick continuity/control cases |
| OSS Runtime + local upstream propagation | `serverless-runtime-upstream-refresh-claims` with invocation, broadcast, Function-log, raw-header, and stable-connection assertions |
| Two-endpoint ownership | `serverless-runtime-upstream-refresh-claims-endpoint{1,2}-owner`; each pass asserts the negotiate audience and refreshed upstream claims for that owner |
