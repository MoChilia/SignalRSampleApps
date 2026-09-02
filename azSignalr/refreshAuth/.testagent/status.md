# Authentication Refresh E2E Status

## Result

The expanded Default-mode suite is green against the local SignalR runtime. Automatic refresh,
old-SDK compatibility, and bounded long-running continuity are opt-in so the normal matrix remains
suitable for iteration.

## Requirement Evidence

| Requirement | Implemented evidence | Validation |
| --- | --- | --- |
| Automatic refresh E2E | `default-auto-dotnet-{ws,sse,lp}`, `default-auto-typescript-{ws,sse,lp}`, `default-auto-short-lifetime-boundary` | Real timer run passed; TypeScript WS/SSE/LP callbacks and service-token rotation are recorded under `.e2e-artifacts/matrix/20260901-102219-37132`. |
| Deadline boundaries | `default-maximum-expiration-{on,off}`, `default-close-disabled`, `default-no-app-expiry`, `default-stale-deadline` | Default matrix passed with absolute `NewExpiration` and token-lifetime assertions. |
| Concurrency strengthening | `default-concurrent-refresh` (10 requests), `default-mixed-lifetime-concurrency` (four different lifetimes) | All requests completed; notification counts matched; final deadline equaled the maximum submitted expiration. |
| Identity and policy | `default-same-user-role-change`, `default-rejected-state`, `default-no-gate-role-change`, user mismatch in transport cases | Role/claim-type fidelity passed; rejected refreshes preserved principal, callback count, deadline, and connection usability. |
| Cross-app-server type 43 | `default-cross-server-type43` | Passed in the fully green Default run under `.e2e-artifacts/matrix/20260902-105743-69984`: app servers A/B used required stickiness, negotiate ran through A, refresh ran through B, and owner A observed the refreshed role and live-connection callback without reconnecting. |
| Protocol and errors | Default/Serverless endpoint errors and negotiate-v0 cases | Default cases passed against the real host/runtime. Serverless cases are present but require a Serverless-configured resource/runtime for execution. |
| Serverless Transient REST | `serverless-transient-{standard,claims-only,rejected-policy,endpoint-errors}` | Automated in the normal Serverless matrix: it verifies the Function host reports `ServiceTransportType=Transient`, then exercises refresh payloads, sentinel preservation, rejection, and endpoint error mapping before the Persistent lane. Execution still requires a configured Serverless resource/runtime. |
| Compatibility | `compatibility-old-sdk-no-advertisement` using the real SDK 1.33.1 host | Passed under `.e2e-artifacts/matrix/20260901-103229-31588`: no lifetime advertisement, no scheduled refresh, connection remained usable. |
| Long-running continuity | `long-running-refresh-continuity`, `long-running-no-refresh-control` | Passed under `.e2e-artifacts/matrix/20260901-103623-31588`: at least two refresh cycles and 60+ gapless ticks; control reconnected with a new ID and reported five lost ticks. |
| Refreshed token adoption | `refreshed-token-adoption-{sse,lp}` | Passed in the fully green long-running run under `.e2e-artifacts/matrix/20260902-110831-69984`: each case refreshed a 45-second service token after 15 seconds, waited 32 seconds past the original issuance, then completed a hub invocation/poll with the same connection ID and no close before the post-expiry status check. |
| OSS Runtime + local upstream propagation | `serverless-runtime-upstream-refresh-claims` | Passed against OSS Service Runtime under `.e2e-artifacts/matrix/20260901-174420-29788`: invocation and broadcast markers changed, raw headers carried both markers, refresh returned 200, and the connection id stayed stable. |
| Two-endpoint ownership | `serverless-runtime-upstream-refresh-claims-endpoint{1,2}-owner` | Passed under `.e2e-artifacts/matrix/20260901-180244-29788`: both endpoints were configured simultaneously; each was explicitly selected for negotiate and proved as owner by the redirect URL, while refresh and claims lookup succeeded through the full multi-endpoint context without reconnecting. |
| Endpoint failover and ambiguity harness | `RefreshConnectionAuth_PreferredOwnerRemoved_FallsBackToRemainingEndpoint`, `RefreshConnectionAuth_MultipleOwners_ReturnsAmbiguousShardingFailure`, `GetConnectionClaims_MultipleOwners_ReturnsAmbiguousShardingFailure` | Passed on net8 and net11: endpoint reload removes the preferred owner and verifies fallback fan-out, while dual-owner responses fail without returning claims or an owning endpoint. |

## Build And Static Validation

- Default host, .NET client, TypeScript client, Function host, and old SDK compatibility host build.
- `run-refresh-auth-e2e-matrix.ps1` passes PowerShell parser validation.
- `run-refresh-auth-e2e-matrix.ps1 -RuntimeUpstreamOnly -SkipBuild -RuntimeConnectionString <local-runtime>` completed with `PASS All automated matrix cases passed`.
- `run-refresh-auth-e2e-matrix.ps1 -RuntimeUpstreamOnly -RuntimeConnectionString <endpoint1> -SecondaryRuntimeConnectionString <endpoint2>` built successfully and completed both named-owner cases with `PASS All automated matrix cases passed`.
- Edited C#, TypeScript, PowerShell, and Markdown files have no editor diagnostics.
- Default core plus `-IncludeLongRunning` completed with `PASS All automated matrix cases passed`.
- Default compatibility case passed in the combined compatibility/long-running run before the original reconnect-delay harness defect was corrected.

## Assertion Quality Review

- Assertions use caller-visible outcomes: HTTP status/body, stable or changed connection ID, callback
  count, absolute expiration, principal snapshots, token rotation, and monotonic tick sequence.
- Negative cases verify state preservation and post-rejection connection usability rather than only
  checking status codes.
- Timer cases wait for real client callbacks; they do not call refresh manually.
- Compatibility uses an actual old SDK package instead of a mock.
- Long-running controls include both positive continuity and expected-loss behavior.

## Remaining Environment-Dependent Coverage

- Windows authentication/`WindowsIdentity`.
- AAD Read/Write data-action authorization combinations.
- Serverless Transient and Persistent execution still require a configured Serverless resource/runtime in the current environment.
- OSS Runtime + local upstream execution remains opt-in and requires the current runtime plus any
  storage required by the local Functions host. `asrs-emulator` is not part of this E2E topology.
- Live-connection ownership migration across runtime failover. Deterministic ownership of each of two
  configured endpoints is covered by the OSS Runtime + local upstream matrix; stale preferred-owner fallback
  and ambiguous ownership are covered by focused SDK harness tests, and cross-app-server type-43 routing is
  covered by the Default matrix.
- Runtime feature-flag restart and cross-pod fault injection.

These remain explicit infrastructure scenarios; no mock is presented as E2E evidence for them.
