# Azure SignalR REST API Authorization Test

This harness calls the authentication-refresh REST APIs with `DefaultAzureCredential`. It does not
store or print access tokens. The credential scope is `https://signalr.azure.com/.default`.

The target Azure environment must have the `2026-07-01` claims and refresh APIs deployed.

## Prerequisites

1. Sign in with the credential you want `DefaultAzureCredential` to select. For local development,
   `az login` is the usual source.
2. Assign that identity one of these roles at the Azure SignalR resource scope:
   - `SignalR REST API Reader` for the `read` profile.
   - `SignalR App Server` for the `write` profile.
   - `SignalR REST API Owner` for the `read-write` profile.
   - No SignalR role for the `none` profile.
3. Wait for role assignment propagation.

The harness intentionally uses an unknown connection token. A 404 proves RBAC passed and connection
lookup ran; a 403 proves RBAC denied the request.

| Expected profile | Claims | Refresh |
| --- | ---: | ---: |
| `read` | 404 | 403 |
| `write` | 403 | 404 |
| `read-write` | 404 | 404 |
| `none` | 403 | 403 |

## Run

```powershell
.\test-authz.ps1 `
  -SignalRName <signalr-name> `
  -ResourceGroup <resource-group> `
  -ExpectedProfile read
```

Or invoke the .NET harness directly:

```powershell
dotnet run --project .\RestApiAuthz.csproj -- `
  --endpoint https://test.service.signalr.net `
  --expected-profile read
```

Run `dotnet run --project .\RestApiAuthz.csproj -- --help` for all options.

## DefaultAzureCredential Selection

`DefaultAzureCredential` follows its normal chain. Environment credentials take precedence over the
Azure CLI credential. Clear stale `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_CLIENT_SECRET`
variables when you intend to test the currently signed-in Azure CLI user.

If both calls return 401, verify the selected identity, tenant, and token scope. If the service
environment and the result cannot be used to evaluate RBAC.
returns 405 for both calls, the refresh APIs are not deployed in that Azure environment; the harness
reports `INCONCLUSIVE` and exits with code 3. That result cannot be used to evaluate RBAC.
