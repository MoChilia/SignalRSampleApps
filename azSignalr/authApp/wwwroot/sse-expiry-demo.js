/**
 * SSE Token Expiry Simulation Client
 *
 * This client demonstrates what happens when an access token expires
 * during an active SSE (Server-Sent Events) connection to Azure SignalR Service.
 *
 * Scenario 1 (BUG):   Static token — reconnect fails with 401
 * Scenario 2 (FIXED): Dynamic token factory — reconnect succeeds automatically
 */

const userIdInput = document.querySelector("#userId");
const roleInput = document.querySelector("#role");
const hubUrlInput = document.querySelector("#hubUrl");
const tokenLifetimeInput = document.querySelector("#tokenLifetime");
const transportSelect = document.querySelector("#transport");
const useStaticTokenCheckbox = document.querySelector("#useStaticToken");
const connectButton = document.querySelector("#connect");
const disconnectButton = document.querySelector("#disconnect");
const sendButton = document.querySelector("#sendToAll");
const messageInput = document.querySelector("#message");
const stateText = document.querySelector("#state");
const countdownText = document.querySelector("#countdown");
const logOutput = document.querySelector("#log");

let connection;
let currentTokenExpiresAt = 0;
let countdownTimer = null;

connectButton.addEventListener("click", connect);
disconnectButton.addEventListener("click", disconnect);
sendButton.addEventListener("click", () => invoke("SendToAll", messageInput.value.trim()));

async function connect() {
  const userId = userIdInput.value.trim();
  const role = roleInput.value.trim();
  const hubUrl = hubUrlInput.value.trim();
  const useStaticToken = useStaticTokenCheckbox.checked;
  const transport = getTransport();

  log(`--- New Connection Attempt ---`);
  log(`[config] transport=${transportName()}, useStaticToken=${useStaticToken}`);

  // Fetch initial token
  const initialTokenResponse = await fetchToken(hubUrl, userId, role);
  const staticToken = initialTokenResponse.accessToken;
  currentTokenExpiresAt = initialTokenResponse.expiresAt;

  log(`[token] initial token issued, expires at ${new Date(currentTokenExpiresAt * 1000).toISOString()} (in ${secondsUntilExpiry()}s)`);

  // Build connection with chosen token strategy
  const builder = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      transport: transport,
      accessTokenFactory: useStaticToken
        // BUG SCENARIO: Returns the same token forever. After expiry, reconnect will fail.
        ? () => {
            log(`[accessTokenFactory] returning STATIC token (expires in ${secondsUntilExpiry()}s)`);
            return staticToken;
          }
        // CORRECT SCENARIO: Fetches a fresh token on every (re)connect.
        : async () => {
            log(`[accessTokenFactory] fetching FRESH token...`);
            const resp = await fetchToken(hubUrl, userId, role);
            currentTokenExpiresAt = resp.expiresAt;
            log(`[accessTokenFactory] got new token, expires in ${secondsUntilExpiry()}s`);
            startCountdown();
            return resp.accessToken;
          }
    })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (retryContext) => {
        log(`[reconnect] attempt #${retryContext.previousRetryCount + 1}, elapsed=${retryContext.elapsedMilliseconds}ms`);
        // Retry up to 3 times with increasing delay
        if (retryContext.previousRetryCount >= 3) {
          log(`[reconnect] giving up after ${retryContext.previousRetryCount} attempts`);
          return null; // stop retrying
        }
        return Math.min(1000 * (retryContext.previousRetryCount + 1), 5000);
      }
    })
    .configureLogging(signalR.LogLevel.Information)
    .build();

  // Wire up event handlers
  builder.on("ReceiveMessage", (senderUserId, message) => {
    log(`[message] from=${senderUserId}: ${message}`);
  });

  builder.onreconnecting((error) => {
    setConnectionState("Reconnecting");
    log(`[reconnecting] ${error?.message ?? "connection lost"}`);
    log(`[reconnecting] token expired=${secondsUntilExpiry() <= 0} (${secondsUntilExpiry()}s remaining)`);
  });

  builder.onreconnected((connectionId) => {
    setConnectionState("Connected");
    log(`[reconnected] new connectionId=${connectionId}`);
    log(`[reconnected] SUCCESS - token was ${useStaticToken ? "STATIC (unexpected!)" : "REFRESHED dynamically"}`);
    startCountdown();
  });

  builder.onclose((error) => {
    setConnectionState("Disconnected");
    stopCountdown();
    if (error) {
      log(`[closed] ERROR: ${error.message}`);
      if (useStaticToken && secondsUntilExpiry() <= 0) {
        log(`[closed] ⚠️ EXPECTED FAILURE: Static token expired, all reconnect attempts failed with 401.`);
        log(`[closed] ⚠️ FIX: Use a dynamic accessTokenFactory that fetches a fresh token.`);
      }
    } else {
      log(`[closed] clean disconnect`);
    }
  });

  connection = builder;

  try {
    setBusy(true);
    await connection.start();
    setConnectionState("Connected");
    log(`[connected] connectionId=${connection.connectionId}, transport=SSE`);
    log(`[connected] token will expire in ${secondsUntilExpiry()}s — watch what happens...`);
    startCountdown();
  } catch (error) {
    setConnectionState("Disconnected");
    log(`[connect failed] ${error.message}`);
  } finally {
    setBusy(false);
  }
}

async function disconnect() {
  if (!connection) return;
  setBusy(true);
  await connection.stop();
  connection = null;
  setBusy(false);
}

async function invoke(methodName, ...args) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    log(`[send] cannot send — not connected (state=${connection?.state})`);
    return;
  }
  try {
    await connection.invoke(methodName, ...args);
    log(`[send] ${methodName}(${args.join(", ")}) — OK`);
  } catch (error) {
    log(`[send failed] ${error.message}`);
    if (error.message.includes("401") || error.message.includes("Unauthorized")) {
      log(`[send failed] ⚠️ Token likely expired. SSE POST requests also require valid tokens.`);
    }
  }
}

// --- Token helpers ---

async function fetchToken(hubUrl, userId, role) {
  const lifetime = Number(tokenLifetimeInput.value) || 30;
  const tokenUrl = new URL("/token", hubUrl).toString();
  const response = await fetch(tokenUrl, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userId, role, lifetimeSeconds: lifetime })
  });
  if (!response.ok) throw new Error(`Token request failed: ${response.status}`);
  return await response.json();
}

function secondsUntilExpiry() {
  return Math.max(0, currentTokenExpiresAt - Math.floor(Date.now() / 1000));
}

// --- Transport helpers ---

function getTransport() {
  switch (transportSelect.value) {
    case "sse": return signalR.HttpTransportType.ServerSentEvents;
    case "websocket": return signalR.HttpTransportType.WebSockets;
    case "longpolling": return signalR.HttpTransportType.LongPolling;
    default: return signalR.HttpTransportType.ServerSentEvents;
  }
}

function transportName() {
  return transportSelect.value || "sse";
}

// --- UI helpers ---

function startCountdown() {
  stopCountdown();
  updateCountdown();
  countdownTimer = setInterval(updateCountdown, 1000);
}

function stopCountdown() {
  if (countdownTimer) {
    clearInterval(countdownTimer);
    countdownTimer = null;
  }
}

function updateCountdown() {
  const remaining = secondsUntilExpiry();
  countdownText.textContent = remaining > 0
    ? `Token expires in ${remaining}s`
    : `⚠️ TOKEN EXPIRED`;
  countdownText.className = remaining > 10 ? "ok" : remaining > 0 ? "warn" : "expired";
}

function setConnectionState(state) {
  stateText.textContent = state;
  stateText.className = state.toLowerCase();
}

function setBusy(busy) {
  connectButton.disabled = busy;
  disconnectButton.disabled = busy;
}

function log(message) {
  const timestamp = new Date().toISOString().substring(11, 23);
  logOutput.textContent += `[${timestamp}] ${message}\n`;
  logOutput.scrollTop = logOutput.scrollHeight;
}
