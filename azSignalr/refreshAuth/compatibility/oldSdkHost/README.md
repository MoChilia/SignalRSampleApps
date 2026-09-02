# Pre-Refresh Azure SignalR SDK Compatibility Host

This host references `Microsoft.Azure.SignalR` 1.33.1, which predates the Azure SignalR
authentication-refresh integration. It runs on the current .NET 11 SignalR server so the current
client can inspect the negotiate response against an updated local runtime.

```powershell
dotnet run -- `
  "--urls=http://localhost:5222" `
  "--Azure:SignalR:ConnectionString=Endpoint=http://localhost;Port=8080;AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;"
```

Run the current client with a 10-second app token and automatic refresh enabled:

```powershell
cd ..\..\refreshAuthClient

dotnet run -- `
  --server-url http://localhost:5222 `
  --user-id alice `
  --role user `
  --transport ws `
  --auto-refresh on `
  --auth-mode token `
  --lifetime-seconds 10 `
  --expiration on `
  --auto-reconnect off
```

Expected:

- `/hub/negotiate` succeeds but omits `tokenLifetimeSeconds`.
- The client does not call `/hub/refresh`, despite auto-refresh being enabled.
- The connection remains open beyond the 10-second app-token expiration because this compatibility
  host deliberately sets `CloseOnAuthenticationExpiration=false`.
