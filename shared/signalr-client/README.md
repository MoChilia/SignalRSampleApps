# Shared SignalR transport client

This Node client can connect to either the self-hosted SignalR transport sample or the Azure SignalR Service transport sample.

Install dependencies:

```powershell
npm install
```

Run against the self-hosted server at `http://localhost:5080/hub`:

```powershell
npm start -- self-hosted websocket
npm start -- self-hosted sse
npm start -- self-hosted long-polling
npm start -- self-hosted auto
npm start -- self-hosted all
```

Run against the Azure SignalR Service-backed server at `http://localhost:5090/hub`:

```powershell
npm start -- azure websocket
npm start -- azure sse
npm start -- azure long-polling
npm start -- azure auto
npm start -- azure all
```

Aliases:

- `self` = `self-hosted`
- `asrs` = `azure`

To use another hub URL, set `HUB_URL`:

```powershell
$env:HUB_URL = "http://localhost:5033/hub"
npm start -- azure websocket
```
