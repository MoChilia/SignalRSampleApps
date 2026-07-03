# Refresh Auth Client (.NET)

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
   `C:\Users\shiyingchen\aspnetcore\artifacts\packages\Debug\Shipping` (version `11.0.0-dev`).
   See [`dev-setup.txt`](dev-setup.txt) for the exact pack commands. The required packages are:

   - `Microsoft.AspNetCore.Connections.Abstractions` (defines `IAuthenticationRefreshFeature`)
   - `Microsoft.AspNetCore.SignalR.Client` (+ `.Client.Core`)
   - `Microsoft.AspNetCore.Http.Connections.Client` (+ `.Common`)
   - `Microsoft.AspNetCore.SignalR.Common` / `.Protocols.Json`

2. The **.NET 11 SDK** from that aspnetcore repo:
   `C:\Users\shiyingchen\aspnetcore\.dotnet\dotnet.exe`.

## Run it end-to-end

### 1. Start the server (`refreshAuthApp`)

The server needs an **AccessKey-style** Azure SignalR connection string and the runtime's
`EnableAuthenticationRefresh` feature flag enabled (see the
[server README](../refreshAuthApp/README.md#running-it) for full details):

```pwsh
cd ..\refreshAuthApp
dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=http://localhost;Port=8080;AccessKey=<base64>;Version=1.0;"
dotnet run --project AzureSignalRRefreshAuthSample.csproj
```

The server listens on <http://localhost:5121> and maps the hub at `/hub`.

### 2. Run this client

```pwsh
cd ..\refreshAuthClient
C:\Users\shiyingchen\aspnetcore\.dotnet\dotnet.exe run -- http://localhost:5121 alice user
```

Arguments (all optional): `run -- <serverBaseUrl> <userId> <role>` (defaults:
`http://localhost:5121 alice user`).

## What you'll see

```
[token] minted app token (expiresIn=60s)
Connecting to http://localhost:5121/hub as 'alice' (user)...
[recv] server: Connected through Azure SignalR Service as alice.
[conn] connected: <connectionId>
Commands: type a message to broadcast (SendToAll), '/refresh' to force a refresh, ...
```

- **Auto-refresh:** demo tokens live ~60s and the client is configured with
  `RefreshBeforeExpiration = 20s`, so ~40s after connect you'll see:

  ```
  [token] minted app token (expiresIn=60s)      # fresh app token for /hub/refresh
  [recv] server: Authentication refreshed on the live connection; still alice.  # ChatHub.OnAuthenticationRefreshedAsync
  [refresh] succeeded at HH:mm:ss; new lifetime=00:01:00
  ```

  The connection is **not** dropped — `AuthenticationExpiresOn` is advanced in place.

- **Manual refresh:** type `/refresh` to call `RefreshAuthenticationAsync()` immediately.
- **Broadcast:** type any other text to invoke `SendToAll`; it echoes back via `ReceiveMessage`.
- **Exit:** press Enter on an empty line (or Ctrl+C).

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
