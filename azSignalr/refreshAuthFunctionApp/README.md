# refreshAuthFunctionApp — Serverless auth-refresh sample

A **.NET isolated Azure Functions** app that acts as the **serverless** auth boundary for Azure
SignalR and exercises the Management SDK's new `ServiceHubContext.RefreshAuthAsync` /
`GetConnectionClaimsAsync`.

Where the Default-mode sample (`../refreshAuthApp`) hosts the hub itself and lets the Azure SignalR
SDK intercept `{hubUrl}/refresh`, this sample owns negotiate **and** refresh in HTTP-triggered
functions that call the Management SDK. The client never talks to a hub server — it connects
directly to the Azure SignalR service and refreshes through these functions.

## What it does

| Route | Function | Purpose |
| --- | --- | --- |
| `POST /token` | `Token` | Mint a demo app-plane token (what the client's `AccessTokenProvider` fetches). |
| `POST /hub/negotiate` | `Negotiate` | Validate the app token, call `ServiceHubContext.NegotiateAsync`, return the ASRS redirect `{ url, accessToken, tokenLifetimeSeconds }`. |
| `POST /hub/refresh?id={connectionToken}` | `Refresh` | Validate the new app token, optionally gate on `GetConnectionClaimsAsync`, call `RefreshAuthAsync`, return `{ accessToken, tokenLifetimeSeconds }`. |
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
dotnet run http://localhost:7071 alice user ws on
```

The client will:
1. `POST /token` to mint an app token (~60s lifetime),
2. `POST /hub/negotiate` with that token → get the ASRS redirect + `tokenLifetimeSeconds`,
3. connect directly to Azure SignalR,
4. ~20s before expiry `POST /hub/refresh?id={connectionToken}` with a freshly minted token →
   `RefreshAuthAsync` advances the connection's auth deadline, so it stays connected gaplessly.

Run with `off` as the last argument to disable auto-refresh and watch the connection get torn down
at the auth deadline instead (`CloseOnAuthenticationExpiration`).

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
`[SignalROutput]` (the Functions SignalR binding, which reads the flat `AzureSignalRConnectionString`
setting — already added to `local.settings.json`).

To make it fire you must configure the upstream and, for local runs, expose the webhook publicly:

0. **Provide storage for the trigger's webhook secret.** Unlike the HTTP functions, the
   `[SignalRTrigger]` needs the host's secret store. `local.settings.json` sets
   `AzureWebJobsStorage=UseDevelopmentStorage=true` (run **Azurite** locally: `azurite`) and
   `AzureWebJobsSecretStorageType=Files` (keeps the webhook key on disk, no storage account needed).
   Without this you'll see *"SignalR trigger is disabled due to 'AzureWebJobsStorage' ..."*. If you
   don't need upstream, just delete `UpstreamFunctions.cs` — the HTTP functions need no storage.
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
   ```
   https://<tunnel-id>.ngrok.io/runtime/webhooks/signalr
   ```
   with Hub rules `*`, Event rules including `messages`, and Auth `None` (demo) or `ManagedIdentity`.

Then `connection.SendAsync("SendToAll", msg)` from a client reaches `SendToAll`, which broadcasts
`ReceiveMessage` back to all clients. Without upstream configured, only the HTTP `/hub/broadcast` path
works for server→client.

## How this maps to the spec

- The Function App is the **auth boundary**: it validates the app token and is the only party that
  can mint service tokens (via the Management SDK).
- `RefreshAuthAsync(connectionToken, expireTime, claims)` returns `{ accessToken,
  tokenLifetimeSeconds }`; the runtime enforces **same-user** (the refreshed
  `ClaimTypes.NameIdentifier` must equal the negotiate `UserId`) and strips reserved claims.
- Refreshed claims propagate to upstream lazily on the next message — there is no proactive
  `refreshed` event.

## Notes

- `RefreshAuthAsync` surfaces failures (unknown connection, user mismatch, service error) as
  `AzureSignalRException`; the sample does best-effort status mapping (`404` / `403` / `400`).
- Running requires Azure Functions Core Tools. If you only want to compile-check against the local
  Management SDK, `dotnet build` is enough.
- The optional `SendToAll` upstream trigger uses the Functions SignalR binding (released
  `Microsoft.Azure.Functions.Worker.Extensions.SignalRService`), which runs in the Functions host
  alongside the Management SDK code — they are independent, so mixing them is fine.
