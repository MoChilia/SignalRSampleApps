const userIdInput = document.querySelector("#userId");
const roleInput = document.querySelector("#role");
const hubUrlInput = document.querySelector("#hubUrl");
const targetUserIdInput = document.querySelector("#targetUserId");
const messageInput = document.querySelector("#message");
const connectButton = document.querySelector("#connect");
const disconnectButton = document.querySelector("#disconnect");
const refreshAuthRestButton = document.querySelector("#refreshAuthRest");
const sendToUserButton = document.querySelector("#sendToUser");
const sendToSelfButton = document.querySelector("#sendToSelf");
const sendToAllButton = document.querySelector("#sendToAll");
const stateText = document.querySelector("#state");
const connectionIdText = document.querySelector("#connectionId");
const countdownText = document.querySelector("#countdown");
const tokenOutput = document.querySelector("#token");
const logOutput = document.querySelector("#log");

let connection;
let httpClient; // captures the negotiate connectionToken so {hubUrl}/refresh can target the live connection.
let currentToken = null;
let currentExpiresAt = 0; // unix seconds
let countdownTimer = null;

// signalR.DefaultHttpClient subclass that snoops the Azure SignalR service negotiate response.
// The service-side negotiate returns { connectionId, connectionToken, availableTransports, ... };
// the JS client only exposes connectionId, so we grab connectionToken here. It refreshes itself
// whenever the client renegotiates (e.g. automatic reconnect).
// SignalR should handle id?=connectionToken in HttpConnection for the refresh auth feature, replace to the real method after the new signalR version is released.
class ConnectionTokenCapturingHttpClient extends signalR.DefaultHttpClient {
  constructor() {
    super(signalR.NullLogger.instance);
    this.connectionToken = null;
    this.connectionId = null;
  }

  async send(request) {
    const response = await super.send(request);
    if (
      response.statusCode === 200 &&
      typeof request.url === "string" &&
      /\/negotiate(\?|$)/.test(request.url) &&
      typeof response.content === "string"
    ) {
      try {
        const body = JSON.parse(response.content);
        if (body && typeof body.connectionToken === "string") {
          this.connectionToken = body.connectionToken;
          this.connectionId = body.connectionId ?? this.connectionId;
        }
      } catch (_) {
        // Not JSON or not a service negotiate response — ignore.
      }
    }
    return response;
  }
}

connectButton.addEventListener("click", connect);
disconnectButton.addEventListener("click", disconnect);
refreshAuthRestButton.addEventListener("click", refreshAuthDefaultMode);
sendToUserButton.addEventListener("click", sendToUser);
sendToSelfButton.addEventListener("click", sendToSelf);
sendToAllButton.addEventListener("click", sendToAll);

async function connect() {
  const userId = userIdInput.value.trim();
  const role = roleInput.value.trim();
  const hubUrl = hubUrlInput.value.trim();

  await fetchInitialToken(hubUrl, userId, role);

  httpClient = new ConnectionTokenCapturingHttpClient();

  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      // Always hand SignalR the freshest token we have. On (re)connect this lets the new
      // exp flow into the service negotiate; for an existing WebSocket the service runtime
      // is responsible for honoring a pushed RefreshAuthMessage (Phase 1 of the service runtime).
      accessTokenFactory: () => currentToken,
      // Custom HttpClient so we can grab the negotiate `connectionToken` and forward it as
      // the `id` on /api/refresh (spec §3 accepts connectionIdOrToken; the token form lets
      // the service route to the owning pod without cross-pod forwarding).
      httpClient,
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on("ReceiveMessage", (senderUserId, message) => {
    log(`[ReceiveMessage] from=${senderUserId}: ${message}`);
  });

  connection.onreconnecting((error) => {
    setStatus("reconnecting", error ? `Reconnecting: ${error.message ?? error}` : "Reconnecting\u2026");
    log(error ? `[reconnecting] ${error}` : "[reconnecting]");
  });

  connection.onreconnected((connectionId) => {
    setConnected(true);
    log(`[reconnected] connectionId=${connectionId ?? "n/a"}`);
  });

  connection.onclose((error) => {
    setConnected(false, error);
    stopCountdown();
    log(error ? `[closed] ${error}` : "[closed]");
  });

  try {
    setBusy(true);
    log(`[connect] user=${userId}, role=${role}, expiresIn=${secondsUntilExpiry()}s`);
    await connection.start();
    setConnected(true);
    log(`[connected] connectionId=${connection.connectionId ?? "n/a"}`);
    startCountdown();
  } catch (error) {
    setConnected(false);
    log(`[connect failed] ${error}`);
  } finally {
    setBusy(false);
  }
}

async function disconnect() {
  if (!connection) {
    return;
  }

  setBusy(true);
  await connection.stop();
  setBusy(false);
}

// Default mode — the shipped Azure SignalR refresh feature (signalr-refresh-auth.md §2):
//   POST {hubUrl}/refresh?id={connectionToken}
//   Authorization: Bearer {fresh-app-token}
//   200 OK { accessToken, tokenLifetimeSeconds }
//
// The SDK's AuthRefreshMatcherPolicy intercepts {hubUrl}/refresh and runs RefreshHandler: it
// validates the app token, sends a RefreshAuthMessage to the runtime (which advances
// AuthenticationExpiresOn in place — no reconnect), and returns a refreshed SERVICE accessToken.
// We mint a fresh app token first (the IdP stand-in) so its exp becomes the new expireTime.
async function refreshAuthDefaultMode() {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  const hubUrl = hubUrlInput.value.trim();
  // Default mode keys the refresh on the negotiate connectionToken (captured by
  // ConnectionTokenCapturingHttpClient). connectionId is not accepted — the runtime resolves
  // the live connection only via the token form of id={connectionToken}.
  const connectionToken = httpClient?.connectionToken;
  if (!connectionToken) {
    log("[refresh skipped] default mode: no connectionToken captured from negotiate");
    return;
  }

  refreshAuthRestButton.disabled = true;
  try {
    // 1) Acquire a fresh APP token (extended exp) from the IdP stand-in (/token).
    const userId = userIdInput.value.trim();
    const role = roleInput.value.trim();
    const tokenResponse = await fetch(new URL("/token", hubUrl).toString(), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userId, role }),
    });
    if (!tokenResponse.ok) {
      log(`[refresh failed] default mode: token mint http=${tokenResponse.status}`);
      return;
    }
    const freshAppToken = (await tokenResponse.json()).accessToken;

    // 2) POST the fresh app token to the SDK-intercepted {hubUrl}/refresh.
    const refreshUrl = `${hubUrl.replace(/\/$/, "")}/refresh?id=${encodeURIComponent(connectionToken)}`;
    log("[refresh] default mode POST {hubUrl}/refresh?id=<connectionToken>");
    const response = await fetch(refreshUrl, {
      method: "POST",
      headers: { "Authorization": `Bearer ${freshAppToken}` },
    });

    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      // Spec §7: { error } — refresh_disabled / invalid_token / connection_not_found /
      // permission_change_rejected / windows_identity_not_supported / internal_server_error.
      log(`[refresh failed] default mode http=${response.status} error=${body.error ?? "?"}`);
      return;
    }

    // 3) Adopt the refreshed SERVICE accessToken so the next (re)connect uses it; reset the countdown.
    applyToken({ accessToken: body.accessToken, expiresIn: body.tokenLifetimeSeconds });
    log(`[refresh ok] default mode tokenLifetimeSeconds=${body.tokenLifetimeSeconds}; connection still ${connection.state}`);
  } catch (error) {
    log(`[refresh failed] default mode ${error}`);
  } finally {
    refreshAuthRestButton.disabled = !connection || connection.state !== signalR.HubConnectionState.Connected;
  }
}

function sendToUser() {
  return invoke("SendToUser", targetUserIdInput.value.trim(), messageInput.value.trim());
}

function sendToSelf() {
  return invoke("SendToCurrentUser", messageInput.value.trim());
}

function sendToAll() {
  return invoke("SendToAll", messageInput.value.trim());
}

async function invoke(methodName, ...args) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  log(`[send] ${methodName}(${args.map((arg) => JSON.stringify(arg)).join(", ")})`);
  await connection.invoke(methodName, ...args);
}

async function fetchInitialToken(hubUrl, userId, role) {
  const tokenUrl = new URL("/token", hubUrl).toString();
  const response = await fetch(tokenUrl, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userId, role })
  });

  if (!response.ok) {
    throw new Error(`Token request failed: ${response.status}`);
  }

  applyToken(await response.json());
}

function applyToken(tokenResponse) {
  currentToken = tokenResponse.accessToken;
  currentExpiresAt = tokenResponse.expiresAt ?? (Math.floor(Date.now() / 1000) + (tokenResponse.expiresIn ?? 0));
  tokenOutput.textContent = `exp=${new Date(currentExpiresAt * 1000).toISOString()} (in ${secondsUntilExpiry()}s)\n${currentToken}`;
}

function secondsUntilExpiry() {
  return Math.max(0, currentExpiresAt - Math.floor(Date.now() / 1000));
}

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
  countdownText.textContent = "exp in --s";
}

function updateCountdown() {
  const remaining = secondsUntilExpiry();
  countdownText.textContent = `exp in ${remaining}s`;
  countdownText.classList.toggle("warn", remaining > 0 && remaining <= 15);
  countdownText.classList.toggle("expired", remaining === 0);
}

function setConnected(isConnected, error) {
  if (isConnected) {
    setStatus("connected", `Connected as ${userIdInput.value.trim()}`);
  } else if (error) {
    setStatus("error", `Disconnected: ${error.message ?? error}`);
  } else {
    setStatus("disconnected", "Disconnected");
  }
  connectionIdText.textContent = isConnected && connection?.connectionId
    ? `connectionId=${connection.connectionId}`
    : "";
  userIdInput.disabled = isConnected;
  roleInput.disabled = isConnected;
  hubUrlInput.disabled = isConnected;
  connectButton.disabled = isConnected;
  disconnectButton.disabled = !isConnected;
  refreshAuthRestButton.disabled = !isConnected;
  sendToUserButton.disabled = !isConnected;
  sendToSelfButton.disabled = !isConnected;
  sendToAllButton.disabled = !isConnected;
}

function setStatus(kind, text) {
  stateText.textContent = text;
  stateText.dataset.status = kind; // "connected" | "reconnecting" | "disconnected" | "error"
}

function setBusy(isBusy) {
  const connected = connection?.state === signalR.HubConnectionState.Connected;
  connectButton.disabled = isBusy || connected;
  disconnectButton.disabled = isBusy || !connected;
  refreshAuthRestButton.disabled = isBusy || !connected;
  sendToUserButton.disabled = isBusy || !connected;
  sendToSelfButton.disabled = isBusy || !connected;
  sendToAllButton.disabled = isBusy || !connected;
}

function log(message) {
  const time = new Date().toLocaleTimeString();
  logOutput.textContent += `[${time}] ${message}\n`;
  logOutput.scrollTop = logOutput.scrollHeight;
}
