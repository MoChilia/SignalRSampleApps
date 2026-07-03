# Azure SignalR Refresh Auth Sample

A sample that demonstrates the **refresh-auth** scenario from
[`signalr-refresh-auth.md`](../../../../azure-signalr-specs/specs/signalr-refresh-auth.md).

The hub opts into the shipped **Default-mode** feature
(`HttpConnectionDispatcherOptions.EnableAuthenticationRefresh = true`), so the Azure SignalR SDK's
`AuthRefreshMatcherPolicy` intercepts `{hubUrl}/refresh` and runs the flow in spec §2. This is
**net11-only** (the option and the SDK's `RefreshHandler` are guarded by `#if NET11_0_OR_GREATER`),
so the sample targets `net11.0`.

## What the runtime feature does (Phase 1)

When a SignalR client is authenticated with a short-lived JWT and the app has opted in to
`CloseOnAuthExpiration`, the Azure SignalR Service runtime tracks the `ExpireTime` and aborts
the connection when it elapses. Phase 1 adds a server-push refresh path:

1. The app server pushes `RefreshAuthMessage { ConnectionIdOrToken, ExpireTime = <new> }` to the
   Azure SignalR Service via the existing service connection.
2. The runtime resolves the connection, calls
   `SignalRClientConnectionContext.TryRefreshAuthentication(newExp)` which advances
   `CloseOnAuthExpirationFeature.ExpiresOn` **monotonically and atomically**.
3. The heartbeat that would have aborted the connection at the original `ExpireTime` now sees the
   advanced value and lets the connection live on — no reconnect, no token round-trip from
   the browser, no message loss.

Phase 1 acks: `Ok` on advance, `NotFound` for unknown connections.

## What this sample shows

The **Refresh (/hub/refresh)** button drives the shipped Default-mode path:

1. The browser mints a fresh app token from `/token` (the IdP stand-in) so its `exp` is the new
   expiration, then POSTs it to `{hubUrl}/refresh?id={connectionToken}` with
   `Authorization: Bearer {fresh-app-token}`. The `connectionToken` is captured from the negotiate
   response by `ConnectionTokenCapturingHttpClient`.
2. `AddAzureSignalR()` auto-registers `AuthRefreshMatcherPolicy`, which intercepts `/hub/refresh`
   and dispatches to `RefreshHandler<ChatHub>`: it validates the app token, runs the optional
   `OnAuthenticationRefresh` gate, and sends a `RefreshAuthMessage` to the runtime — which advances
   `AuthenticationExpiresOn` in place (**no reconnect**) and returns the post-refresh claims.
3. The handler mints a refreshed **service** access token and responds
   `200 OK { accessToken, tokenLifetimeSeconds }`. The browser adopts it for the next (re)connect,
   and `ChatHub.OnAuthenticationRefreshedAsync` posts a notice on the live connection.

The hub sets `CloseOnAuthenticationExpiration = true`, so without a refresh the connection is
aborted at the JWT's `exp`; the refresh advances that deadline in place. A short-lived JWT
(`AppTokenProvider.DefaultLifetime` = 60 s) makes this easy to observe, and the countdown shows the
remaining lifetime.

### Failure mapping

Matches spec §7:

| Failure                                                          | HTTP |
| --------------------------------------------------------------- | ---- |
| App bearer token missing / invalid / expired                    | 401  |
| `OnAuthenticationRefresh` rejects (`permission_change_rejected`) | 403  |
| Connection not found (`connection_not_found`)                   | 404  |
| ASRS / app server error                                         | 500  |

## Running it

> **Prerequisite (.NET 11 preview 7 SDK).** The Azure SignalR SDK's `net11.0` build uses the refresh
> APIs (`EnableAuthenticationRefresh`, `AuthenticationRefreshContext`, `IConnectionUserRefreshFeature`)
> from the `Microsoft.AspNetCore.App` shared framework. Build/run with a **.NET 11 preview 7 (or later)
> SDK** — the version pinned by azure-signalr's `global.json` — whose shared framework already contains
> them; a plain `dotnet build` / `dotnet run` works. (Older preview-6 SDKs fail with `CS0246`.)
> `Microsoft.AspNetCore.Authentication.JwtBearer` is referenced at `11.0.0-dev` from the local feed in
> [`NuGet.config`](NuGet.config), since no stable `11.0.0` exists while .NET 11 is in preview.
>
> If you hit `CS2012: Cannot open ...AzureSignalRRefreshAuthSample.dll for writing ... used by another
> process`, a reused MSBuild worker node is holding the output — run `dotnet build-server shutdown`
> (optionally delete `obj`/`bin`) and rebuild.

1. Set the Azure SignalR connection string via user secrets or env var:

   ```pwsh
   dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
   # or
   $env:Azure__SignalR__ConnectionString = "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
   ```

2. **Enable the service-side feature flag.** The runtime gates the refresh path behind the
   `EnableAuthenticationRefresh` feature flag — if it is off, the service returns
   `404 connection_not_found` even when the connection exists. For a local
   `OSSServices-SignalR-Service` runtime, set:

   ```jsonc
   // appsettings.Development.json
   "Features": {
     "EnableAuthenticationRefresh": true
   }
   ```

3. Build & run:

   ```pwsh
   dotnet run --project AzureSignalRRefreshAuthSample.csproj
   ```

4. Open <http://localhost:5121/> in a browser.
5. Click **Connect**. The countdown shows the JWT's remaining lifetime.
6. Before the countdown hits zero, click **Refresh (/hub/refresh)** — the browser mints a fresh
   app token and POSTs it to `{hubUrl}/refresh`, which the SDK intercepts and applies in place.

## Files

| File                                | Purpose                                                                          |
|-------------------------------------|----------------------------------------------------------------------------------|
| `Program.cs`                        | Web host, JWT auth, `/token`, hub mapping with `EnableAuthenticationRefresh`.     |
| `AppTokenProvider.cs`               | HS256 JWT issuance with configurable lifetime.                                   |
| `ChatHub.cs`                        | Authorized hub; exposes `WhoAmIExp` for diagnostics.                             |
| `NameIdentifierUserIdProvider.cs`   | Maps NameIdentifier / `sub` to SignalR user id.                                  |
| `wwwroot/index.html` + `client.js`  | UI with countdown + the `/hub/refresh` button.                                   |

## Differences vs `authApp`

- JWT lifetime defaults to 60 s (vs 1 h) so the refresh flow is observable.
- `ServiceOptions.AccessTokenLifetime` is matched to the app JWT lifetime.
- `JwtBearer.ClockSkew = TimeSpan.Zero` so the short token is honored as-issued.
- Opts into the shipped Default-mode refresh feature (`EnableAuthenticationRefresh` +
  `OnAuthenticationRefresh`); the SDK intercepts `{hubUrl}/refresh`.
- `/token` advertises `tokenLifetimeSeconds` (spec §6).
- Enables `CloseOnAuthenticationExpiration` on the hub mapping so the service arms its
  heartbeat-based abort at the JWT's `exp`.
- Adds a UI countdown and a `/hub/refresh` button.
- `ChatHub.WhoAmIExp` returns the `exp` from the principal currently seen by the hub
  pipeline — handy for verifying which token the service used on a given invocation.
