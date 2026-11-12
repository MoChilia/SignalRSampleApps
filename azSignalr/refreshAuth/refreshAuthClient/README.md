# Refresh Auth Client (.NET)

<!-- markdownlint-disable MD060 -->

A minimal console client that connects to the [`refreshAuthApp`](../refreshAuthApp) server and
exercises the **Default-mode authentication refresh** feature of the ASP.NET Core SignalR .NET
client (`WithAuthenticationRefresh` + `{hubUrl}/refresh`).

Unlike the browser client bundled with `refreshAuthApp` (which POSTs to `/hub/refresh` by hand),
this client uses the **built-in** `WithAuthenticationRefresh` API, so it auto-schedules the refresh
from the `tokenLifetimeSeconds` the server reports and adopts the refreshed service token
transparently. It is wired to consume a **locally-built** aspnetcore SignalR client (the package
that carries this feature) via [`NuGet.config`](NuGet.config).

## Prerequisites

1. **A local aspnetcore build with the refresh-auth client change**, packed into
  `<aspnetcore-root>\artifacts\packages\Debug\Shipping` (version `11.0.0-dev`).
   See [`dev-setup.txt`](dev-setup.txt) for the exact pack commands. The required packages are:

   - `Microsoft.AspNetCore.Connections.Abstractions` (defines `IAuthenticationRefreshFeature`)
   - `Microsoft.AspNetCore.SignalR.Client` (+ `.Client.Core`)
   - `Microsoft.AspNetCore.Http.Connections.Client` (+ `.Common`)
   - `Microsoft.AspNetCore.SignalR.Common` / `.Protocols.Json`

2. The **.NET 11 SDK** from that aspnetcore repo:
  `<aspnetcore-root>\.dotnet\dotnet.exe`.

## Run it end-to-end

### 1. Start the server (`refreshAuthApp`)

The server needs an **AccessKey-style** Azure SignalR connection string and the runtime's
`EnableAuthenticationRefresh` feature flag enabled (see the
[server README](../refreshAuthApp/README.md#running-it) for full details):

```pwsh
cd ..\refreshAuthApp
dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
dotnet run --project AzureSignalRRefreshAuthSample.csproj
```text

The server listens on <http://localhost:5121> and maps the hub at `/hub`.

### 2. Run this client

```pwsh
cd ..\refreshAuthClient
<aspnetcore-root>\.dotnet\dotnet.exe run -- --server-url http://localhost:5121 --user-id alice --role user
```

Run `dotnet run -- --help` for all options. Named arguments support both `--name value` and
`--name=value`; the legacy positional form remains supported for existing scripts.

- `--transport` = `ws` | `sse` | `lp` | `all`
- `--auto-refresh` = `on` | `off`
- `--auth-mode` = `token` (JWT bearer, default) | `cookie` (cookie auth) | `none` (anonymous failure)
- `--lifetime-seconds` overrides the app token lifetime for negotiate and refresh.
- `--expiration` = `on` | `off`; `off` issues a JWT without `exp` (token mode only).
- `--scenario` selects a Serverless Management SDK negotiate/refresh case. Default mode ignores this
  extra token-request property. See the Function sample README for the supported values.
- `--auto-reconnect` = `on` | `off`; `off` leaves the connection closed after a transport drop instead
  of renegotiating. It is independent of `autorefresh`.
- `--reconnect-delay-seconds` sets a fixed delay before each automatic reconnect attempt. When
  omitted, the SignalR client's default reconnect policy is used. A five-second delay makes the
  message-continuity loss demo deterministic.
- `--negotiate-version` = `0` | `1`; omitted uses the SignalR client's default. Version 0 establishes
  with `connectionId` and is useful for verifying that token-keyed refresh/claims APIs return 404.
- `--ignore-refreshed-access-token` = `on` | `off`; `on` strips the additive `accessToken` field
  before the SignalR client parses a successful refresh response. This simulates a refresh-aware
  client version that predates refreshed service-token adoption for compatibility testing.

### Cookie auth mode

`authmode cookie` uses cookie authentication instead of the bearer `/token` flow: the client POSTs
`/login` once to obtain an auth cookie and reuses it for negotiate/refresh (via a shared
`CookieContainer`). Cookie auth sets the ticket's `ExpiresUtc` itself, so the server needs no
`OnTokenValidated` hook. Run it with:

```pwsh
<aspnetcore-root>\.dotnet\dotnet.exe run -- http://localhost:5121 alice user ws on cookie
```

Because the cookie's claims (including `marker`) are fixed for the cookie's lifetime, cookie-mode
refresh is **expiration-only** — the deadline advances via the cookie's sliding expiration, but the
claim set is unchanged, so `ChatHub.OnAuthenticationRefreshedAsync` does **not** fire (unlike token
mode, whose per-mint `marker` makes every refresh a claims-changed refresh).

## What you'll see

```text
[token] minted app token (expiresIn=60s)
Connecting to http://localhost:5121/hub as 'alice' (user)...
[recv] server: Connected through Azure SignalR Service as alice.
[conn] connected: <connectionId>
Commands: type a message to broadcast (SendToAll), '/refresh' to force a refresh, ...
```

- **Auto-refresh:** demo tokens live ~60s and the client is configured with
  `RefreshBeforeExpiration = 20s`, so ~40s after connect you'll see:

  ```text
  [token] minted app token (expiresIn=60s)      # fresh app token for /hub/refresh
  [recv] server: Authentication refreshed on the live connection; still alice.  # ChatHub.OnAuthenticationRefreshedAsync
  [refresh] succeeded at HH:mm:ss; new lifetime=00:01:00
  ```

  The connection is **not** dropped — `AuthenticationExpiresOn` is advanced in place.

- **Manual refresh:** type `/refresh` to call `RefreshAuthenticationAsync()` immediately.
- **Broadcast:** type any other text to invoke `SendToAll`; it echoes back via `ReceiveMessage`.
- **Exit:** press Enter on an empty line (or Ctrl+C).

The client logs every negotiate/refresh status, advertised `tokenLifetimeSeconds`, decoded service
JWT `exp - iat`, UTC+8 `iat`/`exp` timestamps, and audience. `/status` prints the live hub principal, effective host configuration,
last negotiate/refresh summary, and the latest successful service access token's decoded claims. It
keeps the raw Unix `iat`/`exp` values and adds `iatUtcPlus8`/`expUtcPlus8` display fields. It does not
retain or print the raw signed token.

## Scenario commands

These commands cover the locally reachable Default-mode rows in the E2E checklist:

| Command | Scenario / expected result |
|---|---|
| `/refresh` | Normal claims + expiration refresh; connection ID remains unchanged. |
| `/marker`, then `/refresh`, then `/marker` | Per-mint marker changes and `OnAuthenticationRefreshedAsync` fires. |
| `/refresh-as alice blocked` | Gate rejection: `403 permission_change_rejected`; state remains unchanged. |
| `/refresh-as bob user` | Same-user enforcement: `403 permission_change_rejected`. |
| `/refresh-as alice admin`, then `/status` | Same-user claim update and role/name claim-type fidelity. |
| `/refresh-lifetime 120`, then `/refresh-lifetime 30` | Monotonic deadline: the second stale expiration does not regress the effective deadline. |
| `/refresh-no-exp`, then `/status` | Claims-only refresh: sends no new app expiration (`MaxValue` sentinel internally), preserving the deadline. |
| `/refresh-invalid` | Invalid app bearer token: `401 invalid_token`. |
| `/parallel 2` | Concurrent refresh serialization for the same connection. |
| `/probe get` | Non-POST refresh request: `405`. |
| `/probe missing-header` | POST without connection-token header: `400 missing_connection_token` (`missing-id` remains an alias). |
| `/probe query-only` | Valid query `id` but no connection-token header: `400 missing_connection_token`. |
| `/probe empty-header` | Empty connection-token header plus valid query `id`: `400 missing_connection_token` (no fallback). |
| `/probe header-and-query` | Valid connection-token header plus unknown query `id`: `200` with refreshed `accessToken` and `tokenLifetimeSeconds` (header wins). |
| `/probe no-auth` | POST without app credentials: `401 invalid_token`. |
| `/probe invalid-auth` | POST with malformed app token: `401 invalid_token`. |
| `/probe unknown` | Unknown header token: `404 connection_not_found` (Serverless: `refresh_failed`). |
| `/claims` | Serverless: call the sample's Management SDK claims endpoint and print current claims/deadline. |
| `/status` | Live identity, role, marker, app `exp`, host switches, service JWT lifetime, and decoded service-token claims. |

Normal probes send `X-SignalR-Connection-Token`, not query `id`, and retain existing hub URL
query parameters. Client-facing refresh never falls back to query `id`. Cross-origin browser
clients must allow this header in the CORS preflight response. Run `header-and-query` after
rejected-state checks because it performs a real refresh.

Run cookie mode for expiration-only refresh (the marker remains unchanged):

```pwsh
dotnet run -- --server-url http://localhost:5121 --user-id alice --role user --transport ws --auto-refresh on --auth-mode cookie
```

Run without an app-auth expiration; the negotiated service token uses the host's configured maximum:

```pwsh
dotnet run -- --server-url http://localhost:5121 --user-id alice --role user --transport ws --auto-refresh on --auth-mode token --lifetime-seconds 60 --expiration off
```

Run anonymous negotiation or disable auto-refresh to cover their respective failure/close paths:

```pwsh
dotnet run -- --server-url http://localhost:5121 --user-id alice --role user --transport ws --auto-refresh on --auth-mode none
dotnet run -- --server-url http://localhost:5121 --user-id alice --role user --transport ws --auto-refresh off --auth-mode token
```

Disable both authentication refresh and automatic reconnect to observe the terminal `Closed` state
at the authentication deadline:

```pwsh
dotnet run -- --server-url http://localhost:5121 --user-id alice --role user --transport ws --auto-refresh off --auth-mode token --lifetime-seconds 60 --expiration on --scenario standard --auto-reconnect off
```

Transport continuity uses the same client with `ws`, `sse`, or `lp`. Start the host with
`--Tick:Enabled=true`; refresh on should be gapless, while refresh off reports `[LOST]` after
close/reconnect. For the loss control, add `--reconnect-delay-seconds 5`; without an explicit delay,
the default immediate reconnect can complete between one-second ticks and show no gap. Host-level
option matrices (`Enabled`, close, gate, app lifetime, service lifetime) are listed in the
[server README](../refreshAuthApp/README.md#running-it).

Multi-endpoint ownership/fan-out, cross-pod routed updates, Windows identity, negotiate v0, AAD data
actions, and the runtime feature flag cannot be synthesized by these single-endpoint local samples;
they still require the environments identified in the E2E checklist.

## How the two credential planes work here

- The `AccessTokenProvider` (in `Program.cs`) mints a **fresh app token** from the server's `/token`
  endpoint on every call. This app token is used for `/negotiate` and for `POST {hubUrl}/refresh`.
- At the negotiate redirect, the client captures the **service token** issued by Azure SignalR and
  uses it for the transport (WebSocket/SSE/long-polling) — never the app token.
- On refresh, the client posts to the **original** app URL (`/hub/refresh`) with a fresh app token,
  and adopts the refreshed service token from the `200 OK { accessToken, tokenLifetimeSeconds }`
  response for the next (re)connect.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `TypeLoadException: Could not load type '...IAuthenticationRefreshFeature'` | `Connections.Abstractions 11.0.0-dev` wasn't packed, so restore fell back to the published preview. Pack it (see [`dev-setup.txt`](dev-setup.txt)), clear the NuGet cache for that package, then `dotnet restore --force`. |
| `HttpRequestException: No connection could be made ... (localhost:5121)` | The `refreshAuthApp` server isn't running. Start it first (step 1). |
| `401 Unauthorized` on connect | The `/token` app token was rejected — confirm the server is the `refreshAuthApp` sample and its signing key matches. |
| `warning NU1603 ... Extensions.Features 11.0.0-dev was not found` | Harmless — that package is unchanged, so the published preview satisfies the reference. |
