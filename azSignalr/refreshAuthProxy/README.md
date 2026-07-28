# Refresh Auth Cross-App-Server Proxy

A dependency-free local HTTP reverse proxy for the cross-app-server authentication-refresh test.
It routes `POST /hub/refresh` to app server B and every other app-plane request to app server A.
The SignalR transport itself connects directly to Azure SignalR after negotiate and does not pass
through this proxy.

## Start app servers

Run both app servers against the same Azure SignalR endpoint. Required server stickiness makes a
client negotiated by A remain owned by A while B handles its refresh HTTP request.

The commands below also clear the reloadable sample endpoints currently present in
`refreshAuthApp/appsettings.json`, keeping this test on one service endpoint.

App A:

```powershell
cd ..\refreshAuthApp

dotnet run -- `
  "--urls=http://localhost:5122" `
  "--Azure:SignalR:ConnectionString=Endpoint=http://localhost;Port=8080;AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;" `
  "--Azure:SignalR:ConnectionString:0:runtimeA:primary=" `
  "--Azure:SignalR:ConnectionString:1:runtimeD:primary=" `
  "--Azure:SignalR:ServerStickyMode=Required"
```

App B:

```powershell
cd ..\refreshAuthApp

dotnet run -- `
  "--urls=http://localhost:5123" `
  "--Azure:SignalR:ConnectionString=Endpoint=http://localhost;Port=8080;AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;" `
  "--Azure:SignalR:ConnectionString:0:runtimeA:primary=" `
  "--Azure:SignalR:ConnectionString:1:runtimeD:primary=" `
  "--Azure:SignalR:ServerStickyMode=Required"
```

Wait until both hosts report their service connections online.

## Start proxy

```powershell
cd ..\refreshAuthProxy
node .\proxy.mjs
```

Defaults:

```text
listen: http://localhost:5121
app A:  http://localhost:5122
app B:  http://localhost:5123
```

Override them when needed:

```powershell
node .\proxy.mjs `
  --listen http://localhost:5121 `
  --app-a http://localhost:5122 `
  --app-b http://localhost:5123
```

## Run test

```powershell
cd ..\refreshAuthClient

dotnet run -- `
  --server-url http://localhost:5121 `
  --user-id alice `
  --role user `
  --transport ws `
  --auto-refresh off `
  --auth-mode token `
  --lifetime-seconds 120 `
  --expiration on `
  --scenario standard `
  --auto-reconnect off
```

Enter:

```text
/status
/refresh-as alice admin
/status
```

The proxy log must show negotiate and token requests going to A, and refresh going to B:

```text
POST /token -> A
POST /hub/negotiate?... -> A
POST /hub/refresh?... -> B
```

Expected behavior:

- The connection ID remains unchanged.
- B handles the refresh HTTP request.
- Azure SignalR sends `UpdateConnectionClaimsMessage` type 43 to owning app server A.
- A runs `ChatHub.OnAuthenticationRefreshedAsync` and sends the refreshed marker notification.
- The second `/status` reports role `admin` and the new marker from A.

Use token authentication for this test. Cookie authentication would require sharing ASP.NET Core data
protection keys between A and B.
