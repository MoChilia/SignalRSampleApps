/**
 * Node.js script to simulate SSE token expiry with Azure SignalR.
 *
 * Usage:
 *   npm install @microsoft/signalr node-fetch
 *   node simulate-sse-expiry.js [hubUrl] [tokenLifetimeSeconds]
 *
 * This connects using SSE transport with a short-lived token and logs
 * the full lifecycle: connect → token expiry → disconnect → reconnect attempt.
 */

const signalR = require("@microsoft/signalr");

const HUB_URL = process.argv[2] || "http://localhost:5120/hub";
const TOKEN_LIFETIME = parseInt(process.argv[3]) || 30; // seconds
const USE_STATIC_TOKEN = process.argv[4] === "--static"; // pass --static to simulate the bug

function log(msg) {
  const ts = new Date().toISOString().substring(11, 23);
  console.log(`[${ts}] ${msg}`);
}

async function fetchToken(hubUrl, userId, role, lifetimeSeconds) {
  const tokenUrl = new URL("/token", hubUrl).toString();
  const resp = await fetch(tokenUrl, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userId, role, lifetimeSeconds })
  });
  if (!resp.ok) throw new Error(`Token request failed: ${resp.status}`);
  return await resp.json();
}

async function main() {
  const userId = "node-sim-user";
  const role = "user";

  log(`=== SSE Token Expiry Simulation ===`);
  log(`Hub URL: ${HUB_URL}`);
  log(`Token lifetime: ${TOKEN_LIFETIME}s`);
  log(`Mode: ${USE_STATIC_TOKEN ? "STATIC token (will fail on reconnect)" : "DYNAMIC token (will succeed on reconnect)"}`);
  log(``);

  // Get initial token
  const initialToken = await fetchToken(HUB_URL, userId, role, TOKEN_LIFETIME);
  const staticToken = initialToken.accessToken;
  log(`[token] Initial token issued, expires in ${initialToken.expiresIn}s`);

  let reconnectCount = 0;

  const connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      transport: signalR.HttpTransportType.ServerSentEvents,
      accessTokenFactory: USE_STATIC_TOKEN
        ? () => {
            log(`[accessTokenFactory] Returning STATIC (possibly expired) token`);
            return staticToken;
          }
        : async () => {
            log(`[accessTokenFactory] Fetching FRESH token...`);
            const resp = await fetchToken(HUB_URL, userId, role, TOKEN_LIFETIME);
            log(`[accessTokenFactory] New token, expires in ${resp.expiresIn}s`);
            return resp.accessToken;
          }
    })
    // No automatic reconnect — we want to observe exactly when the SSE stream dies
    // without reconnect attempts masking it with negotiate 401s.
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on("ReceiveMessage", (sender, message) => {
    log(`[message] ${sender}: ${message}`);
  });

  connection.onreconnecting((error) => {
    log(`[reconnecting] Connection lost: ${error?.message ?? "unknown"}`);
  });

  connection.onreconnected((connectionId) => {
    log(`[reconnected] SUCCESS! New connectionId=${connectionId}`);
    log(`[reconnected] The dynamic token factory provided a valid token for re-negotiation.`);
  });

  connection.onclose((error) => {
    if (error) {
      log(`[closed] ERROR: ${error.message}`);
      if (USE_STATIC_TOKEN) {
        log(``);
        log(`=== SIMULATION RESULT ===`);
        log(`The SSE connection was closed because the access token expired.`);
        log(`Reconnect attempts FAILED because accessTokenFactory returned the same expired token.`);
        log(`Each reconnect triggered a new /negotiate call which was rejected with 401 Unauthorized.`);
        log(``);
        log(`FIX: Make accessTokenFactory return a fresh token by calling your /token endpoint.`);
      }
    } else {
      log(`[closed] Clean disconnect.`);
    }
    process.exit(error ? 1 : 0);
  });

  // Connect
  log(`[connect] Starting SSE connection...`);
  await connection.start();
  log(`[connected] connectionId=${connection.connectionId}`);
  log(`[connected] Waiting for token to expire in ~${TOKEN_LIFETIME}s...`);
  log(``);

  // No client-to-server messages — purely observe the SSE stream (server→client).
  // The ServerPingService pushes messages every 5s; watch if they keep arriving after token expiry.
  log(`[waiting] Only listening for server-pushed messages (no client sends).`);

  // Auto-stop after 2 minutes
  setTimeout(async () => {
    log(`[timeout] 2 minutes elapsed. Stopping.`);
    await connection.stop();
  }, 120_000);
}

main().catch((err) => {
  log(`[fatal] ${err.message}`);
  process.exit(1);
});
