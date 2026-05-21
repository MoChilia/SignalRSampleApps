# Azure SignalR transport sample server

This app hosts the `/hub` endpoint used by the JavaScript transport sample and backs it with Azure SignalR Service.

Configure your Azure SignalR Service connection string with user secrets:

```powershell
dotnet user-secrets set "Azure:SignalR:ConnectionString" "Endpoint=https://<resource>.service.signalr.net;AccessKey=<key>;Version=1.0;" --project AzureSignalRTransportSample.csproj
```

Or set it for the current PowerShell session with an environment variable:

```powershell
$env:Azure__SignalR__ConnectionString = "Endpoint=https://<resource>.service.signalr.net;AccessKey=<key>;Version=1.0;"
```

When connecting to the local Azure SignalR runtime, use SignalR mode in the runtime appsettings:

```json
{
  "ServiceType": "SignalR",
  "HubSettings": {
    "Items": {
      "ChatHub": {}
    }
  }
}
```

`RawWebSockets` mode is for raw WebSocket/Web PubSub-style scenarios. This ASP.NET Core sample uses `AddAzureSignalR()`, which opens Azure SignalR server connections under the `simple` hub. If the local runtime is in `RawWebSockets` mode, the server connection can fail with `404` instead of the expected WebSocket `101` upgrade.

Run the server:

```powershell
dotnet run --project AzureSignalRTransportSample.csproj
```

The app listens at `http://localhost:5090` and maps the hub at `http://localhost:5090/hub`.

Open the browser sample:

```text
http://localhost:5090/browser.html
```

The page lets you choose Auto, WebSockets, Server-Sent Events, or Long Polling and send a `NewMessage` hub invocation through Azure SignalR.

You can also use the shared Node transport client:

```powershell
cd ..\..\shared\signalr-client
npm install
npm start -- azure websocket
npm start -- azure sse
npm start -- azure long-polling
npm start -- azure auto
```

The shared client uses `http://localhost:5090/hub` for the `azure`/`asrs` target and `http://localhost:5080/hub` for the `self-hosted`/`self` target. Set `HUB_URL` to override the selected target URL.
