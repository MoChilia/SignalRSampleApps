# refreshAuthFunctionApp — Serverless auth-refresh sample

A **.NET isolated Azure Functions** app that acts as the **serverless** auth boundary for Azure
SignalR and exercises the Management SDK's new `ServiceHubContext.RefreshConnectionAuthenticationAsync` /
`GetConnectionClaimsAsync`.

Where the Default-mode sample (`../refreshAuthApp`) hosts the hub itself and lets the Azure SignalR
SDK intercept `{hubUrl}/refresh`, this sample owns negotiate **and** refresh in HTTP-triggered
functions that call the Management SDK. The client never talks to a hub server — it connects
directly to the Azure SignalR service and refreshes through these functions.

## What it does

| Route | Function | Purpose |
| --- | --- | --- |
| `POST /token` | `Token` | Mint a demo app-plane token (what the client's `AccessTokenProvider` fetches). |
| `GET /sample-config` | `SampleConfig` | Report transport mode and supported scenario names for client `/status`. |
| `POST /hub/negotiate` | `Negotiate` | Validate the app token, run the scenario's Management SDK negotiate operation, and return the ASRS redirect. |
| `POST /hub/refresh?id={connectionToken}` | `Refresh` | Validate the new app token, optionally gate on current claims, run the scenario's refresh overload, and return a new service token. |
| `GET /hub/claims?id={connectionToken}` | `Claims` | Call `GetConnectionClaimsAsync` so claims and `asrs.s.aeo` can be compared before and after refresh. |
| `POST /hub/broadcast` | `Broadcast` | Server→client push: `ServiceHubContext.Clients.All.SendAsync("ReceiveMessage", user, message)`. Needs no upstream. |
| `SignalRTrigger` (upstream) | `SendToAll` | Client→server: the Azure SignalR service forwards a client's `SendToAll` message to this app's upstream webhook, which broadcasts it back. Requires upstream config (see below). |

`host.json` sets an empty `routePrefix`, so these routes match exactly what the .NET SignalR client
appends to its hub URL. The **existing** `../refreshAuthClient` therefore works against this app
unchanged — just point it at the Function App's base URL.

The app tokens use the same issuer / audience / signing key as `refreshAuthApp`, so tokens are
interchangeable across both samples.

## Prerequisites

- .NET 8 SDK (the Management SDK targets `net8.0`; this sample builds against the locally-built
  `Microsoft.Azure.SignalR.Management` via `ProjectReference`).
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
  to run locally (`func start`).
- An Azure SignalR Service resource in **Serverless** mode (connection string).

## Configure

Edit [local.settings.json](local.settings.json) and set your connection string. Config keys use `__`
(which maps to `:` in `IConfiguration`):

```json
"Azure__SignalR__ConnectionString": "Endpoint=https://<name>.service.signalr.net;AccessKey=...;Version=1.0;",
"Azure__SignalR__ServiceTransportType": "Transient"
```

- `Transient` (default) drives negotiate/refresh over the **REST** data plane.
- `Persistent` drives them over the **service-protocol tunnel**. Both paths are supported by the SDK.

## Run

```powershell
cd c:\Users\shiyingchen\SignalRSampleApps\azSignalr\refreshAuthFunctionApp
func start
```

By default the app listens on `http://localhost:7071`.

Then, in another terminal, point the existing client at it:

```powershell
cd ..\refreshAuthClient
dotnet run -- http://localhost:7071 alice user ws on token 60 on standard
```

The client will:

1. `POST /token` to mint an app token (~60s lifetime),
2. `POST /hub/negotiate` with that token → get the ASRS redirect + `tokenLifetimeSeconds`,
3. connect directly to Azure SignalR,
4. ~20s before expiry `POST /hub/refresh?id={connectionToken}` with a freshly minted token →
  `RefreshConnectionAuthenticationAsync` advances the connection's auth deadline, so it stays connected gaplessly.

Set the fifth argument (`autoRefresh`) to `off` to disable auto-refresh and watch the connection get
torn down at the auth deadline instead (`CloseOnAuthenticationExpiration`).

## E2E scenario matrix

The shared client accepts a ninth `scenario` argument and a tenth `autoreconnect` argument:

```text
dotnet run -- <baseUrl> <user> <role> <transport> <autoRefresh> <authMode> <lifetimeSeconds> <expiration> <scenario> <autoReconnect>
```

`scenario` is a test control for this Serverless Function sample. The client includes it in the
JSON body sent to `POST /token`; `AppTokenProvider` writes it into the demo app JWT as the `scenario`
claim. The Function validates that JWT and uses the claim in two places:

- `/hub/negotiate` selects the `NegotiationOptions`, including token maximum, authentication
  deadline, refresh opt-in, close-on-expiration, or ordinary `NegotiateAsync` behavior.
- `/hub/refresh` builds `RefreshConnectionAuthenticationOptions` with a service-token maximum and
  optionally supplies authentication expiration, claims, both, or neither.

The value is fixed for a client process and is sent again whenever its `AccessTokenProvider` mints a
fresh app token. It is intentionally a sample/E2E switch, not a production API or Azure SignalR
protocol field. The Default-mode `refreshAuthApp` accepts the extra `/token` JSON property but ignores
it; `scenario` controls behavior only in `refreshAuthFunctionApp`.

`autoReconnect` defaults to `on`. Set it to `off` to leave the client closed after a transport drop;
this does not change authentication auto-refresh scheduling.

Start the Function host once, then start a fresh client for each row. Use `/claims` before and after
`/refresh` to inspect the runtime claim set without upstream. Use `/status` and `/marker` only when
the upstream webhook is configured, because those are client invocations in Serverless mode.

| Scenario | Client command suffix | Operation and expected observation |
| --- | --- | --- |
| Standard | `60 on standard` | Explicit deadline + claims; `/refresh` updates both. |
| Expiration only | `60 on expiration-only` | Sends expiration with `claims=null`; `/claims` keeps role/marker and advances `asrs.s.aeo`. |
| Claims only | `60 on claims-only` | Sends `expireTime=null` (`MaxValue` on wire); marker changes and `asrs.s.aeo` is preserved. |
| Service-token renewal only | `60 on renewal-only` | Negotiates and refreshes with `TokenLifetime=60s`, no authentication expiration, and close-on-auth disabled. Refresh sends neither expiration nor claims; `/claims` is unchanged, no `asrs.s.aeo` is created, and both initial and refreshed service tokens are about 60 seconds. |
| Non-UTC expiration | `60 on non-utc-expiration` | Sends the same instant with `+08:00`; runtime claim is normalized to UTC Unix time. |
| Explicit refresh sentinel | `60 on max-value-expiration` | Sends `DateTimeOffset.MaxValue`; claims update while deadline is preserved. |
| Past refresh expiration | `60 on past-refresh-expiration` | Negotiate succeeds; `/refresh` returns `400 invalid_expiration` before contacting ASRS. |
| Token maximum wins | `60 on token-maximum-wins` | Negotiated JWT and advertised lifetime are about 30 seconds. |
| Close disabled | `60 on close-disabled` | No close claims/deadline; token refresh remains available. |
| Close disabled + sentinel | `60 on close-disabled-max-value` | Refresh sends `MaxValue`; no deadline or `asrs.s.aeo` is created. |
| Omitted negotiate deadline | `60 on deadline-omitted` | Close is on; legacy deadline is `now + TokenLifetime` (one hour). |
| Explicit no deadline | `60 on deadline-max-value` | Close is on but no close claims/deadline are emitted. |
| Expired negotiate deadline | `60 on past-negotiate-deadline` | `/hub/negotiate` returns `400 expired_authentication_deadline`. |
| Lifetime API without opt-in | `60 on lifetime-api-disabled` | `/hub/negotiate` returns `400 invalid_negotiation_options`. |
| Lifetime API null options | `60 on lifetime-api-null-options` | `/hub/negotiate` returns `400 invalid_negotiation_options`. |
| Ordinary negotiate | `60 on ordinary-negotiate` | Uses `NegotiateAsync`; response omits `tokenLifetimeSeconds` and auto-refresh is not scheduled. |

Example for claims-only refresh:

```powershell
dotnet run -- http://localhost:7071 alice user ws off token 60 on claims-only
```

Then enter:

```text
/claims
/refresh
/claims
/status
```

Use auto-refresh `off` while manually comparing state so the timer cannot race the test. For the
automatic scheduling cases, use `on`.

To test app authentication without `exp`, use expiration `off`. To test Transient versus Persistent,
change `Azure__SignalR__ServiceTransportType` in `local.settings.json` and restart Functions; the same
scenario commands work for both transports.

## Refresh gate demo

The `Refresh` function rejects a **demoted** user: if the new token carries `role = "blocked"` it
calls `GetConnectionClaimsAsync` (the serverless equivalent of Default mode's
`OnAuthenticationRefresh` `PreviousUser` inspection) and returns `403`. This mirrors the policy hook
in `refreshAuthApp`.

## Upstream: client→server messages (optional)

Negotiate / refresh / broadcast are all **server-initiated** and need nothing extra. But if a client
calls `connection.SendAsync("SendToAll", msg)` (client→server), serverless mode has no hub server to
receive it — the Azure SignalR service instead posts that "messages" event to an **upstream** webhook.
[UpstreamFunctions.cs](UpstreamFunctions.cs) provides that handler via `[SignalRTrigger]` +
`[SignalROutput]`. These functions are excluded by default because the Functions SignalR binding only
supports a single endpoint in Transient mode, while this sample's Management SDK configuration can
use multiple endpoints in Persistent mode. Build with `-p:EnableUpstreamFunctions=true` only when
testing upstream against the single endpoint in `UpstreamAzureSignalRConnectionString`; remove the
`Azure__SignalR__Endpoints__*` settings for that run.

To make it fire you must configure the upstream and, for local runs, expose the webhook publicly:

0. **Provide storage for the trigger's webhook secret.** Unlike the HTTP functions, the
   `[SignalRTrigger]` needs the host's secret store. `local.settings.json` sets
   `AzureWebJobsStorage=UseDevelopmentStorage=true` (run **Azurite** locally: `azurite`) and
   `AzureWebJobsSecretStorageType=Files` (keeps the webhook key on disk, no storage account needed).
   Without this you'll see *"SignalR trigger is disabled due to 'AzureWebJobsStorage' ..."*. If you
  don't need upstream, leave `EnableUpstreamFunctions` disabled — the HTTP functions need no storage.
1. **Get the webhook URL.** The Functions SignalR extension serves all triggers at
   `{funcAppBase}/runtime/webhooks/signalr`. Locally that's
   `http://localhost:7071/runtime/webhooks/signalr` (append `?code=<system key>` when a key is required).
2. **Expose it with a tunnel** (the cloud service must reach your machine):

   ```powershell
   func start
   ngrok http 7071            # or: devtunnel host -p 7071
   ```

3. **Point the SignalR resource's Upstream at the tunnel.** In the Azure SignalR resource (Serverless
   mode) → **Settings → Upstream settings**, add a URL template:

    ```text
   https://<tunnel-id>.ngrok.io/runtime/webhooks/signalr
   ```

   with Hub rules `*`, Event rules including `messages`, and Auth `None` (demo) or `ManagedIdentity`.

Then `connection.SendAsync("SendToAll", msg)` from a client reaches `SendToAll`, which broadcasts
`ReceiveMessage` back to all clients. Without upstream configured, only the HTTP `/hub/broadcast` path
works for server→client.

## How this maps to the spec

- The Function App is the **auth boundary**: it validates the app token and is the only party that
  can mint service tokens (via the Management SDK).
- `RefreshConnectionAuthenticationAsync(connectionToken, options)` returns `{ accessToken,
  tokenLifetimeSeconds }`. `options.TokenLifetime` is the refreshed service-token maximum;
  `options.AuthenticationExpiresOn` and `options.Claims` are independently optional. Passing null
  options preserves both and uses the public options type's one-hour token-lifetime default. A
  supplied expiration must be in the future and `TokenLifetime` must be positive; the SDK rejects
  invalid values before contacting ASRS. The runtime enforces **same-user** (the refreshed
  `ClaimTypes.NameIdentifier` must equal the negotiate `UserId`) and strips reserved claims.
- Refreshed claims propagate to upstream lazily on the next message — there is no proactive
  `refreshed` event.

## Notes

- `RefreshConnectionAuthenticationAsync` surfaces failures (unknown connection, user mismatch, service error) as
  `AzureSignalRException`; the sample does best-effort status mapping (`404` / `403` / `400`).
- Running requires Azure Functions Core Tools. If you only want to compile-check against the local
  Management SDK, `dotnet build` is enough.
- The optional `SendToAll` upstream trigger uses the Functions SignalR binding (released
  `Microsoft.Azure.Functions.Worker.Extensions.SignalRService`). Although its APIs are independent
  from the Management SDK, the extension also reads global `Azure:SignalR` configuration and cannot
  index against multiple endpoints in Transient mode. Keep it disabled in the Persistent
  multi-endpoint host; enable it only for the documented single-endpoint upstream build.
