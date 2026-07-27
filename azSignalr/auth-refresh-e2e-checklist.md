# Auth Refresh — E2E Test Checklist

<!-- markdownlint-disable MD060 -->

End-to-end test matrix for the Azure SignalR **authentication refresh** feature. ` ` = to test. "How to exercise" references the local samples: `refreshAuthApp` / `refreshAuthClient` (Default mode) and `refreshAuthFunctionApp` (Serverless mode).

## Run notation

Use two terminals. All commands below assume the Azure SignalR connection strings and runtime feature flag are already configured as described in each sample README.

- `D-host [options]`: from `SignalRSampleApps/azSignalr/refreshAuthApp`, run
  `dotnet run --project AzureSignalRRefreshAuthSample.csproj -- [options]`.
- `D-client [args]`: from `SignalRSampleApps/azSignalr/refreshAuthClient`, run
  `dotnet run -- [args]`. Arguments are
  `<serverBaseUrl> <userId> <role> <transport> <autorefresh> <authmode> <lifetimeSeconds> <expiration> <scenario> <autoreconnect>`;
  defaults are `http://localhost:5121 alice user ws on token <host-default> on standard on`.
- `S-host (Transient)`: in `refreshAuthFunctionApp/local.settings.json`, set
  `Azure__SignalR__ServiceTransportType` to `Transient`, then run `func start` from that folder.
- `S-host (Persistent)`: set the same value to `Persistent`, then run `func start`.
- `S-client [args]`: run the same console client with base URL `http://localhost:7071`, for example
  `dotnet run -- http://localhost:7071 alice user ws off token 60 on standard`. The last argument
  selects a Management SDK scenario; use auto-refresh `off` for controlled `/claims` → `/refresh` comparisons.
- Text beginning with `/` is entered in the running console client. `[Harness]` identifies a case that
  requires a focused SDK/runtime E2E harness or temporary call-site variation not exposed by the samples.

## 1. Core refresh (happy path)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Auto-refresh over WebSockets | `D-host`; `D-client http://localhost:5121 alice user ws on token` | `[refresh] succeeded`, connection never drops, connectionId unchanged |
| ✓ | Auto-refresh over SSE | `D-host`; `D-client http://localhost:5121 alice user sse on token` | same; stream gapless |
| ✓ | Auto-refresh over Long Polling | `D-host`; `D-client http://localhost:5121 alice user lp on token` | same; polls keep working past original `exp` |
| ✓ | Manual refresh | Start either mode/client, record connection ID with `/status`, then enter `/refresh` and `/status` | `RefreshAuthenticationAsync()` returns new lifetime, no reconnect |
| ✓ | Claims-changed refresh | `D-host`; token client; enter `/marker`, `/refresh`, `/marker` | `OnAuthenticationRefreshedAsync` fires; marker changes |
| ✓ | Expiration-only refresh | `D-host`; `D-client http://localhost:5121 alice user ws on cookie`; enter `/marker`, `/refresh`, `/marker` | deadline advances, claims unchanged, `OnAuthenticationRefreshedAsync` does NOT fire |
| ✓  | Claims-only refresh (Serverless) | Start S-host; `S-client ... 60 on claims-only`; enter `/claims`, `/refresh`, `/claims`; optionally send upstream message | claims change; existing auth deadline is preserved; new service token returned |
| ✓  | Service-token renewal only (Serverless) | Start S-host; `S-client ... 60 on renewal-only`; inspect initial lifetime, then enter `/claims`, `/refresh`, `/claims`, `/status` | negotiate and refresh options use `TokenLifetime=60s`, with no authentication expiration and close-on-auth disabled; both tokens are about 60 seconds, claims stay unchanged, and no auth deadline is created |
| ✓  | `tokenLifetimeSeconds` advertised at negotiate | Start D-host/client or S-host/client; read the client's `[http] /hub/negotiate` line and `/status` | Default: eligible authenticated non-Windows principal; Serverless: lifetime API with `EnableAuthenticationRefresh=true` |
| ✓ | Refreshed JWT lifetime matches response | Enter `/refresh`; compare `tokenLifetimeSeconds` and `jwtLifetime` in the client's `[http] /hub/refresh` line | JWT `exp - iat` matches `tokenLifetimeSeconds` within clock/rounding tolerance |
| ✓| Client adopts refreshed accessToken | Enter `/refresh`, wait past the original service-token expiry, then interrupt/restore network to force automatic reconnect | reconnect succeeds past original service-token `exp` (all transports) |

## 2. Auth modes (Default)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓  | JWT bearer mode | `D-host`; `D-client http://localhost:5121 alice user ws on token`; enter `/refresh` | refresh via `/token` app token |
|  ✓ | Cookie auth mode | `D-host`; `D-client http://localhost:5121 alice user ws on cookie`; enter `/refresh` | refresh via `/login` cookie + sliding expiration |
| ✓ | Anonymous connection | Start an authenticated client, then enter `/probe no-auth`; separately run `D-client http://localhost:5121 alice user ws on none` | refresh returns `401 invalid_token`; anonymous negotiate fails and advertises no lifetime |

## 3. Accept/reject gate (`OnAuthenticationRefresh`)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Gate accepts | `D-host`; connect as `alice/user`; enter `/refresh` | refresh applied |
| ✓ | Gate rejects | `D-host`; connect as `alice/user`; record `/status`; enter `/refresh-as alice blocked`, then `/status` | `403 permission_change_rejected`, connection left open unchanged |
| ✓ | `PreviousUser` fetched only when gate configured | Compare runtime traces for default `D-host` versus `D-host --AuthRefresh:GateEnabled=false`; enter `/refresh` in each | `GetConnectionClaims` sent only when a callback is configured |

## 4. Identity & same-user enforcement

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓  | Same-user refresh | Connect as `alice/user`; enter `/refresh-as alice admin`, then `/status` | succeeds |
| ✓ | Different user id in refreshed token | Connect as `alice/user`; record `/status`; enter `/refresh-as bob user`, then `/status` | `403 permission_change_rejected` (`ConnectionUserMismatch`), connection left open |
|   | Windows identity | `[Environment]` Host the Default sample with Windows authentication and connect as a Windows principal; inspect negotiate and enter `/refresh` | `400 windows_identity_not_supported`; negotiate omits lifetime |
| ✓  | Claim-type fidelity | Connect as `alice/user`; enter `/status`, `/refresh-as alice admin`, `/status`; compare `name`, `role`, `isInRole`, `nameClaimType`, `roleClaimType`, `authenticationType` | behave as at connect (nt/rt/aut markers preserved) |

## 5. Close-on-auth-expiration

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓  | `CloseOnAuthExpiration=true`, no refresh | `D-host`; `D-client http://localhost:5121 alice user ws off token`; wait beyond 60 s | connection aborted at `exp`, then `WithAutomaticReconnect` renegotiates (new connectionId) |
| ✓ | `CloseOnAuthExpiration=true`, with refresh | `D-host`; `D-client http://localhost:5121 alice user ws on token`; watch for at least two refresh cycles | deadline advanced in place, never aborted |
| ✓ | `CloseOnAuthExpiration=false` | `D-host --AuthRefresh:CloseOnAuthenticationExpiration=false`; client with auto-refresh off; wait beyond app `exp` | no runtime close deadline is created or advanced; heartbeat never closes |
| ✓ | Close off + app expiry known | Same host option; run token client with auto-refresh on; inspect negotiate JWT/lifetime, then wait beyond app `exp` | service-token lifetime is capped by app expiry; live WebSocket is not closed at that deadline |
| ✓  | Close on + no ticket expiry (Default) | `D-host`; `D-client http://localhost:5121 alice user ws on token 60 off`; enter `/status` and wait beyond 60 s | no `asrs.s.coae`/`asrs.s.aeo`; connection is not closed by auth expiration |
| ✓ | Close on + omitted expiry (Serverless negotiate) | Start S-host; `S-client ... 60 on deadline-omitted`; decode `/status` service-token claims | legacy deadline is `now + TokenLifetime` (one hour); close claims are present |
| ✓ | Close on + explicit no expiry (Serverless negotiate) | Start S-host; `S-client ... 60 on deadline-max-value`; inspect `/status` claims and wait beyond service-token expiry | no close claims and no runtime deadline |
| ✓  | Refresh can't toggle the mode | Compare `standard` and `close-disabled` clients; enter `/claims`, `/refresh`, `/claims` in each | mode fixed at connect; only an existing deadline moves |

## 6. Service-token lifetime and negotiate options

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Default: refresh enabled, app auth expires first | `D-host --AuthRefresh:AppTokenLifetimeSeconds=60 --AuthRefresh:ServiceTokenLifetimeSeconds=3600`; connect token client; inspect `[http]` | negotiated JWT lifetime and `tokenLifetimeSeconds` are about 60 s |
| ✓ | Default: configured token lifetime expires first | `D-host --AuthRefresh:AppTokenLifetimeSeconds=3600 --AuthRefresh:ServiceTokenLifetimeSeconds=60`; connect token client; inspect `[http]` | negotiated JWT lifetime and `tokenLifetimeSeconds` are about 60 s |
| ✓ | Default: refresh disabled | `D-host --AuthRefresh:Enabled=false --AuthRefresh:ServiceTokenLifetimeSeconds=300`; connect token client; inspect `[http]`; enter `/refresh` | JWT uses 300 s; negotiate omits lifetime; refresh returns `404` |
| ✓ | Default: refresh enabled, no app expiry | `D-host --AuthRefresh:ServiceTokenLifetimeSeconds=300`; `D-client http://localhost:5121 alice user ws on token 60 off`; inspect `[http]` | JWT uses configured service-token lifetime; advertised lifetime matches it |
| ✓ | Default refresh token cap | Start with service lifetime 3600 s; connect with 60 s app tokens; enter `/refresh`; inspect refresh `[http]` | refreshed JWT lifetime and returned lifetime both use remaining app lifetime |
| ✓ | Default stale deadline | Connect normally; enter `/refresh-lifetime 120`, `/status`, `/refresh-lifetime 30`, `/status`; compare refresh JWT lifetime | runtime preserves later `asrs.s.aeo`; refreshed JWT uses effective runtime deadline |
| ✓ | Serverless enabled cap | Existing `Functions.Negotiate` uses token max 1 h and app token 60 s; start S-host/client; inspect negotiate `[http]` | negotiated JWT and reported lifetime are about 60 s |
| ✓ | Serverless token maximum wins | Start S-host; `S-client ... 60 on token-maximum-wins`; inspect negotiate `[http]` and `/status` | negotiated JWT and reported lifetime are about 30 s |
| ✓ | Serverless lifetime API disabled | Start S-host; run `S-client ... 60 on lifetime-api-disabled`; inspect failed negotiate body | Function returns `400 invalid_negotiation_options`; SDK throws before token generation |
| ✓ | Serverless lifetime API null options | Start S-host; run `S-client ... 60 on lifetime-api-null-options`; inspect failed negotiate body | Function returns `400 invalid_negotiation_options`; SDK throws before token generation |
| ✓ | Serverless ordinary negotiate | Start S-host; run `S-client ... 60 on ordinary-negotiate`; inspect negotiate `[http]` and wait | legacy negotiate succeeds; `tokenLifetimeSeconds` is omitted and no auto-refresh is scheduled |
| ✓ | Serverless explicit deadline | Start S-host; `S-client ... 60 on non-utc-expiration`; inspect `/status` service-token `asrs.s.aeo` | normalized to UTC; `asrs.s.aeo` stores the same absolute instant |
| ✓ | Serverless expired deadline | Start S-host; run `S-client ... 60 on past-negotiate-deadline`; inspect failed negotiate body | Function returns `400 expired_authentication_deadline` before token generation |
| ✓ | Token and connection deadlines differ | Start S-host; `S-client ... 60 on token-maximum-wins`; inspect JWT `exp-iat` and `asrs.s.aeo` | JWT expires in 30 s; `asrs.s.aeo` remains app deadline at 60 s |

## 7. Optional refresh inputs and sentinel

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Expiration + claims | Start S-host; `S-client ... 60 on standard`; enter `/claims`, `/refresh`, `/claims`; compare marker and `asrs.s.aeo` | both update; new service token returned |
| ✓ | Expiration only | Start S-host; `S-client ... 60 on expiration-only`; enter `/claims`, `/refresh`, `/claims` | deadline advances; existing claims preserved |
| ✓ | Claims only | Start S-host; `S-client ... 60 on claims-only`; enter `/claims`, `/refresh`, `/claims` | SDK sends `DateTimeOffset.MaxValue`; claims update; deadline preserved |
| ✓ | Neither supplied | Start S-host; `S-client ... 60 on renewal-only`; enter `/claims`, `/refresh`, `/claims`, `/status` | refresh options set `TokenLifetime=60s` and omit expiration/claims; SDK sends `MaxValue` + null claims, only the service token is renewed, and `asrs.s.aeo` remains absent |
| ✓ | Non-UTC refresh expiration | Start S-host; `S-client ... 60 on non-utc-expiration`; enter `/claims`, `/refresh`, `/claims` | SDK normalizes to UTC before REST/Persistent dispatch |
| ✓ | Explicit `MaxValue` over Persistent | Start `S-host (Persistent)`; client scenario `claims-only` or `max-value-expiration`; trace `RefreshAuthMessage` and compare `/claims` | runtime skips deadline advancement and preserves `asrs.s.aeo` |
| ✓ | Explicit `MaxValue` over Transient | Start `S-host (Transient)`; client scenario `claims-only` or `max-value-expiration`; capture REST body and compare `/claims` | body contains explicit year-9999 `expireTime`; runtime preserves deadline |
|   | REST body omits `expireTime` | `[Harness]` Send authenticated `POST /api/hubs/chat/connections/:refreshAuth` with claims but no `expireTime` using the REST E2E test client | `400 Bad Request`; omission is not the sentinel |
| ✓ | Existing deadline + `MaxValue` | Start S-host; scenario `max-value-expiration`; enter `/claims`, `/refresh`, `/claims` | existing deadline and `asrs.s.aeo` remain unchanged |
| ✓ | No existing deadline + `MaxValue` | Start S-host; scenario `close-disabled-max-value`; enter `/claims`, `/refresh`, `/claims` | no deadline or `asrs.s.aeo` is created |

## 8. Concurrency & monotonicity

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Concurrent refreshes same connection | Connect normally; enter `/parallel 2`; inspect client and runtime ordering | serialized, no interleave |
| ✓ | Stale / out-of-order refresh | Enter `/refresh-lifetime 120`, then immediately `/refresh-lifetime 30`; compare `/status` and decoded refresh JWT lifetime | `Ok` no-op, deadline not regressed |

## 9. Failure mapping (client-facing)

| ✓ | `error` / result | HTTP | How to exercise |
|---|---|---|---|
| ✓  | `refresh_disabled` | 404 | Start `D-host --AuthRefresh:Enabled=false`; connect client; enter `/refresh` |
| ✓ | (no body) non-POST | 405 | Connect normally; enter `/probe get` |
| ✓ | `missing_connection_token` | 400 | Connect normally; enter `/probe missing-id` |
| ✓ | `invalid_token` | 401 | Enter `/probe no-auth`, `/probe invalid-auth`, and `/refresh-invalid` |
|   | `windows_identity_not_supported` | 400 | Use the Windows-auth environment from section 4; enter `/refresh` |
| ✓ | `permission_change_rejected` (gate) | 403 | Connect as `alice/user`; enter `/refresh-as alice blocked` |
| ✓ | `connection_not_found` | 404 | Connect normally; enter `/probe unknown`; separately test a disconnected/v0 token in harness |
| ✓ | `permission_change_rejected` (mismatch) | 403 | Connect as `alice/user`; enter `/refresh-as bob user` |
|   | `internal_server_error` | 500 | `[Environment]` Route refresh to an app pod that fails after ownership lookup, or inject ASRS/app exception in cross-pod E2E harness |

## 10. Negotiate version

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Negotiate v1 | Use the normal .NET client; confirm negotiate response includes `connectionToken`; enter `/refresh` | refresh works (token→id resolved internally) |
|   | Negotiate v0 | `[Harness]` Negotiate with `negotiateVersion=0`, establish using `connectionId`, then call refresh and claims APIs with that ID | `connection_not_found` (404) on both refresh and claims read |

## 11. Multi-endpoint sharding

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
|   | Known owner | `[Environment]` Configure two endpoints, connect through endpoint B, enter `/refresh`, and correlate endpoint access logs/token `aud` | `RefreshAuthMessage` sent only to owning endpoint; token minted for it |
|   | Unknown owner (fan-out) | `[Harness]` Remove ownership-cache entry for a live connection, refresh, and trace requests to both endpoints | non-owners return `ConnectionNotFound`, one owner succeeds |
|   | Ambiguous multi-owner success | `[Harness]` Stub two endpoints to return success for the same token and invoke refresh | ambiguous-sharding failure (not arbitrary pick) |
|   | Secondary endpoint after failover | Connect to secondary during primary outage, restore primary, then refresh before reconnect | keeps ownership until reconnect |
|   | Dynamic add/remove endpoints | Connect, add/remove an unrelated endpoint through dynamic endpoint config, then refresh and inspect owner | existing connections not moved |
|   | `PreviousUser` read pins subsequent refresh | Enable gate, use unknown-owner setup, refresh with blocked role, then accepted role; correlate claims-read and refresh endpoint logs | claims read + refresh hit the same endpoint |

## 12. Serverless mode (Function App + Management SDK)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Transient (REST) refresh | Start `S-host (Transient)` and S-client; enter `/refresh`; inspect Function/runtime HTTP trace | `POST .../connections/:refreshAuth` → 200 `{claims}` |
| ✓ | Persistent (tunnel) refresh | Start `S-host (Persistent)` and S-client; enter `/refresh`; inspect service-protocol logs | `RefreshAuthMessage` over service connection |
|   | Persistent → REST fallback | Start Persistent, connect, stop/block the Management SDK service connection, then enter `/refresh` | transparently falls back to REST on same endpoint |
| ✓ | `GetConnectionClaims` for `PreviousUser` | Start S-host/client; enter `/refresh-as alice blocked`; inspect Function log beginning `GetConnectionClaimsAsync` | `POST .../connections/claims` returns `{claims}` |
|   | Upstream lazy claims propagation | Configure Azurite + tunnel + ASRS upstream per Function README; enter `/refresh`, then send a chat message; inspect upstream headers/binding claims | next upstream event carries refreshed `X-ASRS-User-Id`/`-Claims` |
|   | AAD data-action authz | `[Environment]` Use AAD credential connection string; assign only Read, call claims; assign only Write, call refresh; remove each role to verify 403 | Read for claims, Write for `:refreshAuth`; 403 if missing |
| ✓ | Negotiate advertises lifetime only when wired | Start S-host/client with existing `EnableAuthenticationRefresh=true`; inspect negotiate `[http]` line | JWT is capped and lifetime is advertised |
| ✓ | Negotiate API rejects missing opt-in | Start S-host; run client scenario `lifetime-api-disabled`; inspect `400 invalid_negotiation_options` | `ArgumentException`; use `NegotiateAsync` instead |
| ✓ | REST sentinel serialization | Start `S-host (Transient)`; client scenario `claims-only` or `renewal-only`; capture REST body | Management SDK sends explicit `DateTimeOffset.MaxValue` |
| ✓ | Persistent sentinel serialization | Start `S-host (Persistent)` with same scenario; inspect protocol trace | `RefreshAuthMessage.ExpireTime == DateTimeOffset.MaxValue` |

## 13. Compatibility

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
|   | Older SDK (no refresh) | Run a pre-refresh Azure SignalR SDK host/client package pair against the updated runtime; inspect negotiate and wait past expiration | no `tokenLifetimeSeconds`; client never calls `/refresh`; live ASRS connection untouched |
|   | Older client (no `accessToken` field) | Use a client build that ignores refresh `accessToken`; refresh, then force reconnect for WS/SSE/LP after original token expiry | ignores it; WS stays alive (deadline extended), LP/SSE/reconnect lapse at original `exp` |
|   | Runtime feature flag off | Disable runtime `Features:EnableAuthenticationRefresh`, restart runtime, connect normally, enter `/refresh` | `connection_not_found` even for a live connection |
|   | Legacy Serverless close fallback | Start S-host; client scenario `deadline-omitted`; decode token and watch close | deadline remains `now + TokenLifetime` (one hour) |
|   | Explicit Serverless no-expiry override | Start S-host; client scenario `deadline-max-value`; decode token and wait beyond service-token expiry | no close deadline, distinguishing it from omitted value |

## 14. Client scheduling

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | Auto-refresh off | `D-host`; `D-client http://localhost:5121 alice user ws off token`; wait beyond `exp` | no timer; abort at `exp` → reconnect |
| ✓ | Automatic reconnect off | `D-host`; `D-client http://localhost:5121 alice user ws off token 60 on standard off`; wait beyond `exp` | no refresh timer and no reconnect attempt; `Closed` fires and the connection remains disconnected |
|   | Short lifetime (`≤ 2×RefreshBefore`) | `D-host --AuthRefresh:AppTokenLifetimeSeconds=30 --AuthRefresh:ServiceTokenLifetimeSeconds=30`; normal auto-refresh client; observe first refresh timestamp | schedules at half lifetime, min-interval enforced |
|   | No `tokenLifetimeSeconds` advertised | `D-host --AuthRefresh:Enabled=false`; connect client with auto-refresh on and watch beyond token lifetime | client never schedules a refresh |
| ✓ | JWT lifetime equals schedule | Start normally; compare each `[http]` `jwtLifetime` with `tokenLifetimeSeconds` for negotiate and refresh | same whole-second lifetime within rounding tolerance |
| ✓ | Manual refresh reschedules timer | Start normally; enter `/refresh` before automatic refresh; compare manual and next automatic refresh timestamps | next auto-refresh is based on newly returned lifetime |

## 15. Message continuity (loss demo)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
|   | Gapless with refresh | `D-host --Tick:Enabled=true --Tick:IntervalSeconds=1`; WS client with auto-refresh on; run for 2+ token lifetimes | no `[LOST]` lines |
|   | Gaps without refresh | Same host; WS client with auto-refresh off; wait through abort/reconnect | `[LOST] n tick(s)` during abort/reconnect window |

## 16. Routed `Context.User` update (Default)

| ✓ | Scenario | How to exercise | Expected |
|---|---|---|---|
| ✓ | `OnAuthenticationRefreshedAsync` on owning server | Token client: enter `/refresh`; observe server message with new marker; then `/status` | runs after claims applied (claims-changed only) |
|   | Cross-pod routed update | `[Environment]` Run two app pods, connect through pod A, route refresh HTTP to pod B, enter `/refresh`, inspect pod A callback/log | owner on a different pod still gets `UpdateConnectionClaimsMessage` |
|   | Push suppression | Cookie client: enter `/marker`, `/refresh`, `/marker`; trace service messages for absence of type 43 | unchanged claims → no type-43 push |
|   | Best-effort | `[Harness]` Inject failure sending `UpdateConnectionClaimsMessage` after runtime refresh succeeds; invoke refresh | failed routed update still acks the client refresh |

---

Rows marked `[Harness]` or `[Environment]` are not directly reachable from the stock single-endpoint
sample run. They state the required temporary call-site change, focused test harness, identity setup,
multi-endpoint topology, cross-pod routing, AAD role assignment, or runtime configuration needed.

## 2026-07-27 local run issues

- **Short-lifetime auto-refresh does not schedule.** With app/service lifetimes set to 10 seconds and
  auto-refresh enabled, the client never called `/hub/refresh`; the runtime closed the connection at
  each deadline and automatic reconnect produced a new connection ID every 10 seconds. The no-refresh,
  no-reconnect control closed once after 10.46 seconds as expected.
- **Refresh-disabled response body is missing.** With `AuthRefresh:Enabled=false`, negotiate correctly
  omitted `tokenLifetimeSeconds` and minted a 300-second service JWT, but manual refresh returned an
  empty 404 response instead of `404 {"error":"refresh_disabled"}`.
- **Serverless user-mismatch error code is inconsistent.** A Persistent-mode refresh from `alice` to
  `bob` correctly returned HTTP 403, but the Function response used `error=refresh_failed` rather than
  `error=permission_change_rejected`. The rejected refresh left the connection claims unchanged.
