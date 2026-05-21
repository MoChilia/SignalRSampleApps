# SignalR transport sample

This sample shows one SignalR JavaScript client connecting to the same hub with:

- WebSockets
- Server-Sent Events
- Long Polling

Transport-specific clients are split into separate files:

- `websocket-client.js`
- `sse-client.js`
- `long-polling-client.js`

Shared connection flow lives in `transport-runner.js`, and shared trace logging lives in `network-trace.js`.

Run the server:

```powershell
dotnet run --project SignalRTransportSample.csproj
```

In another terminal, run the client:

```powershell
npm install
npm start
```

Or open the browser sample after starting the server:

```text
http://localhost:5080/browser.html
```

The browser sample lets you choose Auto, WebSockets, Server-Sent Events, or Long Polling from a dropdown and then send a `NewMessage` hub invocation.

Run one mode at a time by passing it as an argument:

```powershell
npm start -- auto
npm start -- websocket
npm start -- sse
npm start -- long-polling
npm start -- all
```

`auto` omits the `transport` option and lets SignalR choose the best available transport from the negotiate response.

The client prints a small network trace while it runs. Look for:

- `POST /hub/negotiate` before each transport starts
- `WS /hub?...` for the WebSocket connection
- `GET /hub?...` for the Server-Sent Events stream
- `body=<empty>` on the SSE `GET`, because the SSE stream is receive-only
- `body="...NewMessage...<RS>"` on the SSE `POST`, showing the client-to-server hub invocation
- `SSE event data="...messageReceived...<RS>"`, showing the streamed server-to-client response body chunk
- repeated `GET /hub?...` requests for Long Polling
- `POST /hub?...` when the client sends a hub invocation over SSE or Long Polling
- `DELETE /hub?...` when Long Polling stops

The client defaults to `http://localhost:5080/hub`. To use another SignalR server:

```powershell
$env:HUB_URL = "http://localhost:5033/hub"
npm start
```