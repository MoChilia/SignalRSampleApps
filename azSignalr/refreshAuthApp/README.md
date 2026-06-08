# Azure SignalR Refresh Auth Sample

A sample that demonstrates the **refresh-auth** scenario from
[`signalr-refresh-auth.md`](../../../../azure-signalr-specs/specs/signalr-refresh-auth.md),
covered by Phase 1 of the server-side runtime feature.

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

This sample exercises **both Management SDK transports** from spec §3 — transient and
persistent — from a single customer-facing endpoint:

```http
POST /api/refresh?hub={hub}&id={connectionIdOrToken}&mode={transient|persistent}
Authorization: Bearer {app-token}

200 OK
{ "accessToken": "<refreshed-token>", "tokenLifetimeSeconds": 60, "mode": "transient" }
```

Both modes converge on the same runtime handler inside ASRS —
`ClientConnectionLifetimeManager.RefreshClientAuthAsync` → `RefreshLocalClientAuthAsync` →
`SignalRClientConnectionContext.TryRefreshAuthentication`. The transient REST controller
(`HubProxyV20260701Controller.RefreshConnectionAuth`) builds a `RefreshAuthMessage` and
forwards it via the message broker; the persistent transport delivers the same message
over the service connection. So the live connection's `AuthenticationExpiresOn` is
advanced in place either way — **no reconnect**, no token round-trip from the browser,
no message loss. After the service confirms, the app server returns
`{ accessToken, tokenLifetimeSeconds, mode }` so the client's `accessTokenFactory` has a
fresh token ready for the next (re)connect.

### Mode `transient` (default) — REST data plane

The app server validates the bearer token via `JwtBearer`, mints a refreshed app token
(acting as the IdP stand-in), signs an HS256 service token, and POSTs to the ASRS
data-plane:

```http
POST {endpoint}/api/hubs/{hub}/connections/{connectionIdOrToken}/:refresh?api-version=2026-07-01
Authorization: Bearer {service-token}
Content-Type: application/json

{ "expireTime": "<utc>" }
```

On `204 No Content` the service has advanced `AuthenticationExpiresOn` for the live
connection.

### Mode `persistent` — service-protocol tunnel

The app server writes a `RefreshAuthMessage` (service-protocol message type **41**) onto
the SDK's **existing persistent service connection** for `ChatHub` and awaits the
matching `AckMessage`:

```text
Request:  [41, ConnectionIdOrToken, Claims?, ExpireTime, AckId, ExtensionMembers]
Response: AckMessage(AckId, Status, Message, Payload, ExtensionMembers)
```

- `Claims` is `null` in v1 (expiration-only update).
- `AckId` is allocated by the SDK; the value the caller passes is overwritten.
- `AckStatus.Ok` → 200, `AckStatus.NotFound` → 404, other / timeout → 500.

The SDK-side Default-mode plumbing for `/hub/refresh` is Phase 3 in spec §8 and not yet
shipped, so the sample reaches into the SDK's internal
`IServiceConnectionManager<ChatHub>` via reflection to push the message over the same
service connection that backs `SendUserAsync` / `Clients.User(...)`. This is sample-only
plumbing meant to demonstrate the persistent transport — production code should use the
Management SDK's `RefreshAuthAsync` once it ships.

- A short-lived JWT (`AppTokenProvider.DefaultLifetime` = 60 s) drives the
  close-on-auth-expire behavior.
- The countdown shows the JWT's remaining lifetime.
- The chat hub keeps working across the refresh in both modes.

### Failure mapping (`/api/refresh`)

Matches spec §7. Same mapping for both modes:

| Failure                                              | HTTP |
| ---------------------------------------------------- | ---- |
| App bearer token missing / invalid / expired         | 401  |
| ASRS returns `ConnectionNotFound` (REST 404 / `AckStatus.NotFound`) | 404  |
| ASRS cross-pod / other failure, app server exception, ack timeout   | 500  |

## Running it

1. Set the Azure SignalR connection string via user secrets or env var. The
   `/api/refresh` flow requires an **AccessKey-style** connection string (AAD-only
   connection strings are not supported by this sample's REST signer):

   ```pwsh
   dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
   # or
   $env:Azure__SignalR__ConnectionString = "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
   ```

2. **Enable the service-side feature flag.** The runtime gates `/:refresh` behind the
   `EnableAuthRefresh` feature flag — if it is off, the service returns
   `404 Connection ... is not found` for the REST call even when the connection exists.
   For a local `OSSServices-SignalR-Service` runtime, set:

   ```jsonc
   // appsettings.Development.json
   "Features": {
     "EnableAuthRefresh": true
   }
   ```

3. Build & run:

   ```pwsh
   dotnet run --project AzureSignalRRefreshAuthSample.csproj
   ```

4. Open <http://localhost:5121/> in a browser.
5. Click **Connect**. The countdown shows the JWT's remaining lifetime.
6. Before the countdown hits zero, click one of the refresh buttons:
   - **Refresh (Transient / REST)** — `POST /api/refresh?mode=transient` → service `/:refresh`.
   - **Refresh (Persistent / service connection)** — `POST /api/refresh?mode=persistent` →
     `RefreshAuthMessage` over the SDK's existing persistent service connection.

## Files

| File                                | Purpose                                                                          |
|-------------------------------------|----------------------------------------------------------------------------------|
| `Program.cs`                        | Web host, JWT auth, `/token`, `/api/refresh` (transient + persistent), hub mapping. |
| `AppTokenProvider.cs`               | HS256 JWT issuance with configurable lifetime.                                   |
| `ChatHub.cs`                        | Authorized hub; exposes `WhoAmIExp` for diagnostics.                             |
| `NameIdentifierUserIdProvider.cs`   | Maps NameIdentifier / `sub` to SignalR user id.                                  |
| `wwwroot/index.html` + `client.js`  | UI with countdown + Transient and Persistent refresh buttons.                    |


## Notes on the `/api/refresh` endpoint

- Matches the spec customer-facing shape: `POST /api/refresh?hub={hub}&id={connectionIdOrToken}&mode={transient|persistent}`
  with `Authorization: Bearer {app-token}`, returns `{ accessToken, tokenLifetimeSeconds, mode }`.
- The endpoint is `[Authorize]`d; the `JwtBearer` middleware validates the app token before
  the handler runs. The handler then mints a refreshed app token for the validated identity
  and uses its `exp` as the `expireTime` sent to ASRS.
- The data-plane REST path (transient mode) uses the hub name **as registered with the service**,
  which the Azure SignalR server SDK lowercases (see `DefaultServiceEndpointGenerator.GetPrefixedHubName`).
  The sample lowercases the `?hub=...` query value before building the URL —
  `/api/hubs/chathub/...`.
- The `id` query parameter accepts the connection token for negotiate v1 and connection id for negotiate v0 the service resolves token → id via `GetConnectionIdByToken`.
- Transient mode: the bearer token sent to **the service** is an HS256 JWT signed with the
  connection string's `AccessKey`, with `aud` set to the resource URL.
- Persistent mode: the message rides the SDK's already-authenticated service connection, so
  there is no per-request service token to sign.
- For locally-hosted runtimes the connection string typically uses a separate `Port=`
  segment (e.g. `Port=8888`). The sample merges it into the endpoint authority before
  building the REST URL (transient mode only), otherwise the call defaults to port 80/443.
- Negotiate-time advertisement: `/token` returns `tokenLifetimeSeconds` so the client
  knows when to call `/api/refresh`.

## Differences vs `authApp`

- JWT lifetime defaults to 60 s (vs 1 h) so the refresh flow is observable.
- `ServiceOptions.AccessTokenLifetime` is matched to the app JWT lifetime.
- `JwtBearer.ClockSkew = TimeSpan.Zero` so the short token is honored as-issued.
- Adds spec-conformant `POST /api/refresh?hub={hub}&id={connectionIdOrToken}&mode={transient|persistent}`
  that validates the bearer token, applies the refresh on the live connection in one of two
  ways (REST data plane or persistent service-connection `RefreshAuthMessage`), and returns
  `{ accessToken, tokenLifetimeSeconds, mode }`.
- `/token` advertises `tokenLifetimeSeconds` (spec §6).
- Enables `CloseOnAuthenticationExpiration` on the hub mapping so the service arms its
  heartbeat-based abort at the JWT's `exp`.
- Adds a UI countdown and two refresh buttons (transient + persistent).
- `ChatHub.WhoAmIExp` returns the `exp` from the principal currently seen by the hub
  pipeline — handy for verifying which token the service used on a given invocation.
