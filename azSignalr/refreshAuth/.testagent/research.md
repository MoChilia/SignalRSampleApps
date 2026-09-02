# Authentication Refresh E2E Research

## Scope

Expand `run-refresh-auth-e2e-matrix.ps1` using behavior grounded in:

- `azure-signalr-specs/specs/signalr-refresh-auth.md`
- `workspace/notes/azureSignalrAuthRefreshApiProposal.md`
- `workspace/notes/azureSignalrAuthRefreshDefaultApiProposal.md`
- `workspace/notes/azureSignalrAuthRefreshServerlessApiProposal.md`

Requested categories: automatic refresh, deadline boundaries, concurrency, identity/policy,
protocol/errors, and long-running/compatibility.

## Existing Harness

- Default host: `refreshAuthApp`
- Serverless host: `refreshAuthFunctionApp`
- .NET driver: `refreshAuthClient`
- TypeScript driver: `refreshAuthTsClient`
- Compatibility host: `compatibility/oldSdkHost`
- Matrix: `run-refresh-auth-e2e-matrix.ps1`

The matrix already covers manual Default refresh over WS/SSE/LP, TypeScript manual refresh,
cookie auth, same-user role update, endpoint errors, two concurrent refreshes, stale deadlines,
refresh disabled, no app expiration, and a broad Serverless option matrix.

## Static Pairing

The mandatory polyglot pairing analyzer found 14 source files and no conventional test files.
All 14 are statically unpaired because this suite is process-driven PowerShell E2E rather than a
unit-test project. Pairing therefore does not measure runtime coverage; each added behavior needs an
explicit matrix case, output assertion, and successful process run.

## Confirmed Implementation Contracts

- Negotiate v0 refresh is rejected by the runtime as connection-not-found; the current matrix's
  Serverless v0 success expectation is stale.
- Default `Context.User` must not expose `asrs.s.*`; Default expiration is observed through
  `AuthenticationRefreshContext.NewExpiration`.
- Serverless raw claim reads may observe `asrs.s.aeo` because they return runtime service claims.
- Current .NET and TypeScript clients schedule short lifetimes at half-life; current source does not
  implement the proposals' documented 30-second minimum.
- Runtime currently preserves a later deadline when a stale expiration arrives, but may still apply
  refreshed claims. Tests must assert the implemented deadline invariant without claiming a full
  no-op until the product contract is resolved.
- Invalid bearer tokens may be rejected by authentication middleware with a bare 401 before the
  refresh handler can emit an OAuth error body.

## Automatable Without Extra Infrastructure

- Default .NET auto-refresh over WS/SSE/LP with stable connection id and callback/deadline checks.
- TypeScript auto-refresh over WS/SSE/LP with refreshed service-token adoption.
- Maximum-authentication-expiration cap, no-ticket-expiration fallback, close-on-expiration disabled,
  and configured service-token maximum boundaries.
- Equal/earlier/later deadline behavior and higher fan-out concurrency.
- Rejected gate and user mismatch leave user/deadline/callback state unchanged.
- Default and Serverless negotiate-v0 rejection.
- Missing/malformed authentication and endpoint method/token errors.
- Old SDK host does not advertise refresh and a new client does not schedule it.
- Optional tick-stream continuity and no-refresh reconnect control.

## Infrastructure-Dependent

- Windows identity requires an HTTP.sys/IIS Windows-auth host.
- AAD data-action coverage requires identities with Read-only, Write-only, and no-access roles.
- Upstream lazy propagation is automated with the current OSS Service Runtime routing
  `chat/messages/*` to the local Function webhook on port 7071. The Function project
  must be built with `EnableUpstreamFunctions=true`, and the same single-endpoint connection string
  must configure both Management SDK and SignalR binding paths. Persistent transport keeps the
  E2E path on the SDK-to-runtime service protocol.
- `asrs-emulator` is explicitly outside this E2E topology because the requirement is to verify the
  production OSS Runtime's auth-refresh state update together with local HTTP upstream propagation.
- Multi-endpoint propagation is exercised by supplying two endpoint connection strings and running
  the scenario twice with endpoint order reversed. Each pass asserts that negotiate selected the
  expected owning endpoint and that refresh/upstream claims stay on that live connection.
- Multi-endpoint/failover requires two independent resources or runtimes.
- Cross-app-server routed updates require two Default hosts and the proxy topology.
- Cross-pod/internal-server failures require controllable runtime fault injection.

These remain documented as blocked environment scenarios and are not represented by mocks in this
E2E matrix.

## Acceptance Checklist

- [x] Automatic refresh is exercised without manually calling refresh.
- [x] Every auto-refresh case proves callback occurrence, token/principal update, and stable connection.
- [x] Deadline cap/preserve/absent boundaries use absolute expiration evidence.
- [x] Concurrent refresh verifies all requests complete and the deadline does not regress.
- [x] Gate and identity rejections prove state remains unchanged.
- [x] Negotiate v0 and endpoint errors match current runtime/host behavior.
- [x] Old-SDK compatibility proves no refresh advertisement and no scheduled refresh.
- [x] Long-running continuity is opt-in and bounded.
- [x] Host/client/TypeScript builds and PowerShell parsing pass.
- [x] Live Default matrix and compatibility pass; Serverless remains environment-dependent and is reported separately.
- [x] Local upstream propagation has an opt-in case that proves pre/post-refresh markers through
  invocation results, broadcast echoes, Function logs, the raw claims header, and a stable connection id.
- [x] Two supplied endpoints are configured together and each is exercised as the owning endpoint.
