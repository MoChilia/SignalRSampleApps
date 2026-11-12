# refreshWebJobsFunctionApp

<!-- markdownlint-disable MD012 -->

An **in-process** Azure Functions app that tests the `Microsoft.Azure.WebJobs.Extensions.SignalRService`
extension — specifically the new serverless **auth-refresh** binding and the `tokenLifetimeSeconds`
advertisement added for the [SignalR refresh-auth feature](../../../../azure-signalr-specs/specs/signalr-refresh-auth.md).

Unlike [`refreshAuthFunctionApp`](../refreshAuthFunctionApp) (isolated worker, calls the Management SDK
directly), this app uses the **in-process WebJobs bindings** — the same surface JS/Python/Java/PowerShell
Function Apps consume — so refresh is declarative instead of hand-rolling the `:refreshAuth` REST call.

## Endpoints

| Route | Binding | Purpose |
| --- | --- | --- |
| `POST /api/negotiate` | `[SignalRConnectionInfo]` | Explicitly enables authentication refresh and returns `{ url, accessToken, tokenLifetimeSeconds }`. The one-hour service-token maximum lets a refresh-aware client schedule refresh. |
| `POST /api/refresh` | `[SignalRRefresh]` | Reads `X-SignalR-Connection-Token` and renews the live connection's service token without reconnecting; returns `{ accessToken, tokenLifetimeSeconds }`. |
| `POST /api/broadcast` | `[SignalR]` output | Pushes `ReceiveMessage` (user, message) to all clients. Body: `{ "user": "...", "message": "..." }`. |

The client sends its user id via the `x-user-id` header (mapped into the negotiate/refresh claims).
The connection token binds from `{headers.X-SignalR-Connection-Token}` only, with no query `id`
fallback. Cross-origin browser callers must allow this header in the CORS preflight response.

## Prerequisites / build

This sample references the **locally built** extension project and, through it, the **local
`azure-signalr` Management SDK** (which contains the unreleased `RefreshConnectionAuthenticationAsync` /
`GetConnectionClaimsAsync`). That imposes two build requirements:

1. **A net11-capable .NET SDK.** The local `Microsoft.Azure.SignalR` project multi-targets `net11.0`.
   The default SDK on this machine (preview net11) works; there is no `global.json` here so it is picked
   automatically.
2. **Global build properties** (they must flow to *restore*, so they can't be baked into the
   `ProjectReference`):
   - `UseLocalSignalRSdk=true` — makes the extension resolve the local Management SDK projects.
   - `EnableSourceControlManagerQueries=false`, `EnableSourceLink=false`, `DeterministicSourcePaths=false`
     — bypass an unrelated `azure-sdk-for-net` build-infra issue (old `Microsoft.Build.Tasks.Git`
     cannot read a newer git repo format).

Use the helper script (recommended):

```powershell
.\build.ps1            # build
.\build.ps1 -Target run   # build + func start
.\build.ps1 -Target clean # remove bin/obj
```

or the raw command:

```powershell
dotnet build -p:UseLocalSignalRSdk=true -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false -p:DeterministicSourcePaths=false
```

> When the extension ships a Management SDK GA with these APIs, drop the flags and reference the
> extension package normally — the function code is unchanged.

## Refresh lifetime semantics

`SignalRConnectionInfoAttribute.EnableAuthenticationRefresh` must be explicitly enabled; ordinary
connection-info bindings retain legacy negotiate behavior and omit `tokenLifetimeSeconds`.
`TokenLifetimeSeconds` on both bindings is the maximum Azure SignalR **service-token** lifetime. It
is not an application-authentication deadline.

This small binding sample identifies users with `x-user-id` and intentionally does not configure
`AddDefaultAuth`, so no validated app-token expiration is available. Consequently, refresh preserves
the live connection's existing authentication deadline while renewing its service token and claims.
To advance a real application-authentication deadline, configure `AddDefaultAuth`, send a bearer token
with `exp`, and set `CloseOnAuthenticationExpiration = true` on `[SignalRConnectionInfo]`; the binding
then carries the validated token expiration through negotiate and refresh.

## Run

1. Put your Azure SignalR **connection string** in `local.settings.json`
   (`AzureSignalRConnectionString`). Optionally set `AzureSignalRServiceTransportType` to `Transient`
   (default) or `Persistent`.
2. Start the storage emulator (Azurite) — `AzureWebJobsStorage` uses `UseDevelopmentStorage=true`.
3. Start the app (needs [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local)):

   ```powershell
   .\build.ps1 -Target run
   ```

4. Exercise it:

   ```powershell
   # negotiate
   curl -X POST http://localhost:7071/api/negotiate -H "x-user-id: alice"

   # refresh (the connection token the client holds goes only in the header)
   curl -X POST "http://localhost:7071/api/refresh" -H "X-SignalR-Connection-Token: <connectionToken>" -H "x-user-id: alice"

   # broadcast to all clients
   curl -X POST http://localhost:7071/api/broadcast -H "Content-Type: application/json" -d '{"user":"server","message":"hello all"}'
   ```

## Notes

- Server-to-client push (`/broadcast`) works over both transport types. Client-to-server invocation
  (a client calling a hub method) needs a `[SignalRTrigger]` upstream function and the SignalR service's
  upstream URL configured — out of scope for this refresh-focused sample.
- The hub name is `chat` (see `SignalRFunctions.HubName`).

## Known limitation: running locally before the Management SDK ships

This sample builds against local source until `Microsoft.Azure.SignalR.Management` ships a package
containing `NegotiateWithTokenLifetimeAsync`, `RefreshConnectionAuthenticationOptions`, and
`GetConnectionClaimsAsync`. Once that package is available, replace the `ProjectReference` in
[refreshWebJobsFunctionApp.csproj](refreshWebJobsFunctionApp.csproj) with a normal
`PackageReference` and drop the local-build `-p:` flags. The function code does not change.
