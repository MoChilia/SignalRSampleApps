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

This sample exercises the **server-runtime push** refresh-auth flow end-to-end, matching
the customer-facing contract from spec §3 (Serverless mode):

```http
POST /api/refresh?hub={hub}&id={connectionIdOrToken}
Authorization: Bearer {app-token}

200 OK
{ "accessToken": "<refreshed-token>", "tokenLifetimeSeconds": 60 }
```

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
connection — **no reconnect**, no token round-trip from the browser, no message loss.
The app server then returns `{ accessToken, tokenLifetimeSeconds }` so the client's
`accessTokenFactory` has a fresh token ready for the next (re)connect.

- A short-lived JWT (`AppTokenProvider.DefaultLifetime` = 60 s) drives the
  close-on-auth-expire behavior.
- The countdown shows the JWT's remaining lifetime.
- The chat hub keeps working across the refresh.

### Failure mapping (`/api/refresh`)

Matches spec §7:

| Failure                                              | HTTP |
| ---------------------------------------------------- | ---- |
| App bearer token missing / invalid / expired         | 401  |
| ASRS returns `ConnectionNotFound`                    | 404  |
| ASRS cross-pod / other failure, app server exception | 500  |

## Running it

1. Set the Azure SignalR connection string via user secrets or env var. The
   `/api/refresh` flow requires an **AccessKey-style** connection string (AAD-only
   connection strings are not supported by this sample's REST signer):

   ```pwsh
   dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=http://localhost;Port=8888;AccessKey=<base64>;Version=1.0;"
   # or
   $env:Azure__SignalR__ConnectionString = "Endpoint=http://localhost;Port=8888;AccessKey=<base64>;Version=1.0;"
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
6. Before the countdown hits zero, click **Refresh auth (server runtime, /api/refresh)** —
   the spec-conformant server-runtime push path (`/api/refresh` → service `/:refresh`).

## Files

| File                                | Purpose                                                                          |
|-------------------------------------|----------------------------------------------------------------------------------|
| `Program.cs`                        | Web host, JWT auth, `/token`, `/api/refresh`, hub mapping.                       |
| `AppTokenProvider.cs`               | HS256 JWT issuance with configurable lifetime.                                   |
| `ChatHub.cs`                        | Authorized hub; exposes `WhoAmIExp` for diagnostics.                             |
| `NameIdentifierUserIdProvider.cs`   | Maps NameIdentifier / `sub` to SignalR user id.                                  |
| `wwwroot/index.html` + `client.js`  | UI with countdown + **Refresh auth (server runtime, /api/refresh)** button.      |


## Notes on the `/api/refresh` endpoint

- Matches the spec customer-facing shape: `POST /api/refresh?hub={hub}&id={connectionIdOrToken}`
  with `Authorization: Bearer {app-token}`, returns `{ accessToken, tokenLifetimeSeconds }`.
- The endpoint is `[Authorize]`d; the `JwtBearer` middleware validates the app token before
  the handler runs. The handler then mints a refreshed app token for the validated identity
  and uses its `exp` as the `expireTime` sent to ASRS.
- The data-plane REST path uses the hub name **as registered with the service**, which the
  Azure SignalR server SDK lowercases (see `DefaultServiceEndpointGenerator.GetPrefixedHubName`).
  The sample lowercases the `?hub=...` query value before building the URL —
  `/api/hubs/chathub/...`.
- The `id` query parameter accepts the connection token for negotiate v1 and connection id for negotiate v0 the service resolves token → id via `GetConnectionIdByToken`.
- The bearer token sent to **the service** is an HS256 JWT signed with the connection
  string's `AccessKey`, with `aud` set to the resource URL.
- For locally-hosted runtimes the connection string typically uses a separate `Port=`
  segment (e.g. `Port=8888`). The sample merges it into the endpoint authority before
  building the REST URL, otherwise the call defaults to port 80/443.
- Negotiate-time advertisement: `/token` returns `tokenLifetimeSeconds` so the client
  knows when to call `/api/refresh`.

## Differences vs `authApp`

- JWT lifetime defaults to 60 s (vs 1 h) so the refresh flow is observable.
- `ServiceOptions.AccessTokenLifetime` is matched to the app JWT lifetime.
- `JwtBearer.ClockSkew = TimeSpan.Zero` so the short token is honored as-issued.
- Adds spec-conformant `POST /api/refresh?hub={hub}&id={connectionIdOrToken}` that validates the
  bearer token, calls the service `/:refresh` REST API to extend a live connection's auth
  expiration without a reconnect, and returns `{ accessToken, tokenLifetimeSeconds }`.
- `/token` advertises `tokenLifetimeSeconds` (spec §6).
- Enables `CloseOnAuthenticationExpiration` on the hub mapping so the service arms its
  heartbeat-based abort at the JWT's `exp`.
- Adds a UI countdown and a **Refresh auth (server runtime, /api/refresh)** button.
- `ChatHub.WhoAmIExp` returns the `exp` from the principal currently seen by the hub
  pipeline — handy for verifying which token the service used on a given invocation.
