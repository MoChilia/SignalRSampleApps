# Azure SignalR Authentication Refresh Samples & E2E Testing Suite

This directory contains the complete sample applications, test clients, and automated test scripts for the **Azure SignalR Authentication Refresh** feature.

Authentication Refresh allows client connections to renew their application authentication and service access token in place (via `POST {hubUrl}/refresh`) without disconnecting or suffering message loss.

---

## Directory Structure

```text
azSignalr/refreshAuth/
├── README.md                           # This document
├── run-refresh-auth-e2e.ps1            # Quick automated smoke test script
├── run-refresh-auth-e2e-matrix.ps1     # Full automated test matrix script
├── auth-refresh-e2e-checklist.md       # Comprehensive feature checklist & manual test guide
├── refreshAuthApp/                     # Default-mode ASP.NET Core sample host (net11)
├── refreshAuthClient/                  # .NET console test client (supports interactive & batch modes)
├── refreshAuthFunctionApp/             # Serverless-mode Azure Functions sample host (isolated-worker, net8)
├── refreshAuthProxy/                   # Local reverse proxy for cross-app-server refresh testing
├── refreshAuthTsClient/                # TypeScript / Node.js test client using local @microsoft/signalr
├── refreshWebJobsFunctionApp/          # In-process WebJobs Functions sample host
└── compatibility/                      # Compatibility test host using legacy pre-refresh SDK
```

---

## Automated Test Scripts

Two PowerShell scripts automate end-to-end testing against live Azure SignalR endpoints or local runtimes.

### 1. Quick Smoke Test (`run-refresh-auth-e2e.ps1`)

`run-refresh-auth-e2e.ps1` executes a fast, high-level verification of both Default mode and Serverless mode.

#### Smoke Test Coverage

- Host startup and readiness check.
- Client connection over WebSockets.
- Manual authentication refresh (`/refresh`).
- Gate policy acceptance & rejection (`permission_change_rejected`).
- Connection ID stability across refresh cycles.
- HTTP error status mappings (`400`, `401`, `404`, `405`).

#### Smoke Test Usage

```powershell
Set-Location <SignalRSampleApps-root>\azSignalr\refreshAuth

# Run smoke tests against configured connection strings
.\run-refresh-auth-e2e.ps1

# Run with explicit connection string parameters
.\run-refresh-auth-e2e.ps1 -DefaultConnectionString "Endpoint=https://..." -ServerlessConnectionString "Endpoint=https://..."
```

---

### 2. Full Test Matrix (`run-refresh-auth-e2e-matrix.ps1`)

`run-refresh-auth-e2e-matrix.ps1` executes a comprehensive, deterministic test matrix covering transport variants, authentication schemes, optional claims/deadlines, and client implementations.

#### Matrix Test Coverage

- **Default Mode:**
  - Transports: WebSockets (`ws`), Server-Sent Events (`sse`), Long Polling (`lp`).
  - Auth Schemes: JWT Bearer (`token`) and Cookie Authentication (`cookie`).
  - Client SDKs: .NET Client (`refreshAuthClient`) and TypeScript Client (`refreshAuthTsClient`).
  - Edge Cases: Disabled refresh, missing app expiration, gate rejection, same-user enforcement.
  - Cross-server routing: negotiate through app A, refresh through app B, and verify type 43 updates the live principal on owner A.
- **Serverless Mode (Management SDK):**
  - Transport lanes: focused Transient REST coverage followed by the full Persistent tunnel matrix.
  - Positive scenarios: `standard`, `expiration-only`, `claims-only`, `renewal-only`, `non-utc-expiration`, `max-value-expiration`, `close-disabled-max-value`, `token-maximum-wins`.
  - Negative scenarios: `past-refresh-expiration`, `past-negotiate-deadline`.
  - Protocol options: `negotiate-v0` vs `negotiate-v1`, lifetime API disabled, null options defaults.
  - Optional OSS Runtime + local upstream: verifies that the first client message after refresh carries the refreshed claims from the runtime to the Function trigger without reconnecting.

#### Matrix Test Usage

```powershell
Set-Location <SignalRSampleApps-root>\azSignalr\refreshAuth

# Run the complete test matrix
.\run-refresh-auth-e2e-matrix.ps1

# Run Default mode only
.\run-refresh-auth-e2e-matrix.ps1 -SkipServerlessMode

# Run Serverless mode only
.\run-refresh-auth-e2e-matrix.ps1 -SkipDefaultMode

# Skip rebuilding sample projects (useful when running iteratively)
.\run-refresh-auth-e2e-matrix.ps1 -SkipBuild

# Include real timer-driven automatic refresh over WS/SSE/LP for .NET and TypeScript
.\run-refresh-auth-e2e-matrix.ps1 -IncludeAutoRefresh

# Include the actual SDK 1.33.1 compatibility host
.\run-refresh-auth-e2e-matrix.ps1 -IncludeCompatibility

# Include bounded two-cycle message-continuity and no-refresh control cases
.\run-refresh-auth-e2e-matrix.ps1 -IncludeLongRunning

# Run only OSS Runtime + local upstream propagation (the runtime must already be running)
.\run-refresh-auth-e2e-matrix.ps1 -RuntimeUpstreamOnly
```

---

## Script Parameters

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `-DefaultConnectionString` | String | `$env:DEFAULT_SIGNALR_CONNECTION_STRING` | Azure SignalR connection string for Default-mode tests. |
| `-ServerlessConnectionString` | String | `$env:SERVERLESS_SIGNALR_CONNECTION_STRING` | Azure SignalR connection string for Serverless-mode tests. |
| `-RuntimeConnectionString` | String | Local OSS Runtime on port `8888` | Runtime endpoint used by both the Management SDK and Function SignalR bindings when `-IncludeRuntimeUpstream` is set. The former `-UpstreamConnectionString` name remains an alias. |
| `-SecondaryRuntimeConnectionString` | String | Empty | Optional second OSS Runtime/Azure SignalR connection string. When supplied, the focused case configures both endpoints and runs twice with their order reversed, proving refresh and upstream propagation when each endpoint owns the client. Requires Persistent transport. |
| `-SkipDefaultMode` | Switch | `False` | Skips Default-mode host and client test cases. |
| `-SkipServerlessMode` | Switch | `False` | Skips Serverless Function App host and client test cases. |
| `-SkipBuild` | Switch | `False` | Skips `dotnet build` and `npm build` steps before running test cases. |
| `-IncludeAutoRefresh` | Switch | `False` | Adds timer-driven .NET and TypeScript cases over WS, SSE, and Long Polling, plus the short-lifetime boundary. |
| `-IncludeCompatibility` | Switch | `False` | Adds the real SDK 1.33.1 host case and verifies no refresh advertisement or scheduling. |
| `-IncludeLongRunning` | Switch | `False` | Adds bounded multi-cycle tick continuity, no-refresh reconnect/loss controls, and SSE/Long Polling refreshed-token adoption past the original token expiry. |
| `-IncludeRuntimeUpstream` | Switch | `False` | Requires OSS Service Runtime, builds and indexes the optional SignalR triggers, uses Persistent Management SDK transport, and verifies pre/post-refresh claims through local upstream invocations and broadcasts. The former `-IncludeUpstream` name remains an alias. |
| `-RuntimeUpstreamOnly` | Switch | `False` | Implies `-IncludeRuntimeUpstream -SkipDefaultMode` and runs only the focused Runtime + upstream case. The former `-UpstreamOnly` name remains an alias. |
| `-DotNetPath` | String | `"dotnet"` | Path to the `dotnet` executable. |
| `-NodePath` | String | `"node"` | Path to the `node` executable. |
| `-TimeoutSeconds` | Int | `120` (matrix) / `90` (smoke) | Maximum seconds to wait for host readiness or client execution. |

---

## Prerequisites

### OSS Runtime + local upstream E2E

This case intentionally uses the OSS Service Runtime, not `asrs-emulator`. Start the Function host
through the matrix and run the current `OSSServices-SignalR-Service` runtime on port `8888` in
Serverless mode with an upstream template for hub `chat`, category `messages`, and all events.
From the `OSSServices-SignalR-Service` repository, start the runtime in a separate terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:PortOffset = "808"
$env:ServiceType = "SignalR"
$env:ServiceMode = "Serverless"
$env:UpstreamSettings__Templates__0__HubPattern = "chat"
$env:UpstreamSettings__Templates__0__CategoryPattern = "messages"
$env:UpstreamSettings__Templates__0__EventPattern = "*"
$env:UpstreamSettings__Templates__0__UrlTemplate = "http://localhost:7071/runtime/webhooks/signalr"
$env:DNS__Addresses__localhost = "127.0.0.1"
dotnet run --project .\src\Microsoft.Azure.SignalR.ServiceRuntime --no-build --no-restore --no-launch-profile
```

The upstream target is:

```text
http://localhost:7071/runtime/webhooks/signalr
```

Then run the focused case from this directory:

```powershell
.\run-refresh-auth-e2e-matrix.ps1 -RuntimeUpstreamOnly
```

To test two endpoints, start two Serverless OSS Runtime instances with the same upstream template
(or configure two Azure SignalR resources to use the Function webhook), then supply both connection
strings. The matrix configures both endpoints in `Azure:SignalR:Endpoints` and runs once with each
named endpoint explicitly selected for negotiation. Claims lookup and refresh continue to use the
full multi-endpoint context, so each pass verifies correct owner discovery and pinning:

```powershell
.\run-refresh-auth-e2e-matrix.ps1 -RuntimeUpstreamOnly `
  -RuntimeConnectionString "<endpoint-1-connection-string>" `
  -SecondaryRuntimeConnectionString "<endpoint-2-connection-string>"
```

For two local runtimes, use different ports—for example `PortOffset=810` for port `8890` and
`PortOffset=811` for port `8891`. Both runtimes must route `chat/messages/*` to the same Function
webhook. For Azure-hosted endpoints, the webhook must be publicly reachable.

The matrix starts the Function host on port `7071`, builds it with
`EnableUpstreamFunctions=true`, and supplies this runtime connection string to both the Management
SDK and the Function SignalR bindings:

```text
Endpoint=http://localhost;Port=8888;AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;
```

The Function host uses the checked-in `AzureWebJobsStorage=UseDevelopmentStorage=true` setting, so
start Azurite when the local Functions runtime requires storage for trigger metadata. No public
tunnel is needed because the OSS Runtime and Function host run on the same machine. Stop any
`asrs-emulator` process already using port `8888` before starting the runtime.

1. **Azure SignalR Connection Strings:**

   - Configure user secrets for `refreshAuthApp`:

     ```powershell
     dotnet user-secrets set "Azure:SignalR:ConnectionString" "<connection-string>" --project .\refreshAuthApp\AzureSignalRRefreshAuthSample.csproj
     ```

   - Or set environment variables:

     ```powershell
     $env:DEFAULT_SIGNALR_CONNECTION_STRING = "<default-connection-string>"
     $env:SERVERLESS_SIGNALR_CONNECTION_STRING = "<serverless-connection-string>"
     ```

2. **.NET 11 SDK & Node.js:**

   - .NET 11 SDK is required for `refreshAuthApp` and `refreshAuthClient` (pre-configured via `global.json`).
   - Node.js (v18+) is required for `refreshAuthTsClient` and `refreshAuthProxy`.

---

## Test Artifacts and Diagnostics

All automated test runs output detailed logs under `.e2e-artifacts/`:

```text
refreshAuth/.e2e-artifacts/
├── default-host.stdout.log           # Default host stdout (smoke test)
├── default-client.stdout.log         # Default client stdout (smoke test)
├── serverless-host.stdout.log        # Serverless host stdout (smoke test)
├── serverless-client.stdout.log      # Serverless client stdout (smoke test)
└── matrix/
    └── <timestamp>-<process-id>/     # Isolated matrix run directory
        ├── default-token-ws.client.stdout.log
        ├── default-typescript-ws.client.stdout.log
        ├── serverless-standard.client.stdout.log
        └── ...
```

If any test case fails, the script reports the failure reason, outputs a summary table, and prints the path to the corresponding log files for troubleshooting.
