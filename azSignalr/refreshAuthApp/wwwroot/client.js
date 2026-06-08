const userIdInput = document.querySelector("#userId");
const roleInput = document.querySelector("#role");
const hubUrlInput = document.querySelector("#hubUrl");
const extendSecondsInput = document.querySelector("#extendSeconds");
const targetUserIdInput = document.querySelector("#targetUserId");
const messageInput = document.querySelector("#message");
const connectButton = document.querySelector("#connect");
const disconnectButton = document.querySelector("#disconnect");
const toggleCloseOnAuthExpButton = document.querySelector("#toggleCloseOnAuthExp");
const refreshAuthRestButton = document.querySelector("#refreshAuthRest");
const refreshAuthPersistentButton = document.querySelector("#refreshAuthPersistent");
const sendToUserButton = document.querySelector("#sendToUser");
const sendToSelfButton = document.querySelector("#sendToSelf");
const sendToAllButton = document.querySelector("#sendToAll");
const stateText = document.querySelector("#state");
const connectionIdText = document.querySelector("#connectionId");
const countdownText = document.querySelector("#countdown");
const tokenOutput = document.querySelector("#token");
const logOutput = document.querySelector("#log");

let connection;
let httpClient; // captures the negotiate connectionToken so /api/refresh can target the connection without cross-pod forwarding.
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
toggleCloseOnAuthExpButton.addEventListener("click", toggleCloseOnAuthExp);
refreshAuthRestButton.addEventListener("click", () => refreshAuthVia("transient"));
refreshAuthPersistentButton.addEventListener("click", () => refreshAuthVia("persistent"));
sendToUserButton.addEventListener("click", sendToUser);
sendToSelfButton.addEventListener("click", sendToSelf);
sendToAllButton.addEventListener("click", sendToAll);

// Pull the current CloseOnAuthenticationExpiration value from the server on page load so
// the toggle button reflects the actual HttpConnectionDispatcherOptions state.
refreshCloseOnAuthExpLabel().catch(() => { /* server not up yet — ignored */ });

// Flips HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration on the server.
// The new value applies at the next negotiate; existing WebSockets keep their original arm state
// (the service-side abort is set at negotiate time from the negotiate claim).
async function toggleCloseOnAuthExp() {
  toggleCloseOnAuthExpButton.disabled = true;
  try {
    const current = toggleCloseOnAuthExpButton.getAttribute("aria-pressed") === "true";
    const next = !current;
    const hubUrl = hubUrlInput.value.trim();
    const url = new URL("/api/options/closeOnAuthExp", hubUrl).toString();
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: next }),
    });
    if (!response.ok) {
      log(`[options] toggle failed http=${response.status}`);
      return;
    }
    const body = await response.json().catch(() => ({}));
    applyCloseOnAuthExpLabel(Boolean(body.closeOnAuthExp));
    log(`[options] CloseOnAuthenticationExpiration=${body.closeOnAuthExp} (applies at next negotiate — reconnect to observe)`);
  } finally {
    toggleCloseOnAuthExpButton.disabled = false;
  }
}

async function refreshCloseOnAuthExpLabel() {
  const hubUrl = hubUrlInput.value.trim();
  const url = new URL("/api/options/closeOnAuthExp", hubUrl).toString();
  const response = await fetch(url);
  if (!response.ok) {
    return;
  }
  const body = await response.json().catch(() => ({}));
  applyCloseOnAuthExpLabel(Boolean(body.closeOnAuthExp));
}

function applyCloseOnAuthExpLabel(enabled) {
  toggleCloseOnAuthExpButton.setAttribute("aria-pressed", String(enabled));
  toggleCloseOnAuthExpButton.textContent = `CloseOnAuthExp: ${enabled ? "ON" : "OFF"}`;
}

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

// Mirrors azure-signalr-specs/specs/signalr-refresh-auth.md §3 (Serverless mode):
//   POST /api/refresh?mode={transient|persistent}&hub={hub}&id={connectionIdOrToken}
//   Authorization: Bearer {app-token}
//   200 OK { accessToken, tokenLifetimeSeconds, mode }
//
// Both modes converge on the same runtime handler
// (`ClientConnectionLifetimeManager.RefreshClientAuthAsync` → `RefreshLocalClientAuthAsync` →
//  `SignalRClientConnectionContext.TryRefreshAuthentication`):
//   - transient  : the app server signs a service token and POSTs /:refresh; the ASRS REST controller
//                  builds a RefreshAuthMessage and forwards it via the message broker.
//   - persistent : the app server writes the same RefreshAuthMessage over its existing service connection.
// Either way the ASRS runtime advances CloseOnAuthExpirationFeature.ExpiresOn in place; the browser
// keeps the same WebSocket and gets a refreshed accessToken for the next (re)connect.
async function refreshAuthVia(mode) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  const hubUrl = hubUrlInput.value.trim();
  const extendSeconds = Number(extendSecondsInput.value) || 60;
  // Refresh-auth requires the negotiate `connectionToken` (captured by
  // ConnectionTokenCapturingHttpClient). connectionId is NOT a valid fallback — the service
  // only resolves the live connection via the token form of `id={connectionIdOrToken}`.
  const connectionToken = httpClient?.connectionToken;
  if (!connectionToken) {
    log(`[api/refresh skipped] mode=${mode}: no connectionToken captured from negotiate`);
    return;
  }

  const button = mode === "persistent" ? refreshAuthPersistentButton : refreshAuthRestButton;
  button.disabled = true;
  try {
    log(`[api/refresh] mode=${mode} POST id=<connectionToken> +${extendSeconds}s`);
    const url = new URL(
      `/api/refresh?mode=${encodeURIComponent(mode)}&hub=ChatHub&id=${encodeURIComponent(connectionToken)}&additionalSeconds=${extendSeconds}`,
      hubUrl
    ).toString();
    const response = await fetch(url, {
      method: "POST",
      headers: {
        // Spec §3: app token in Authorization: Bearer header (validated by JwtBearer middleware).
        "Authorization": `Bearer ${currentToken}`,
      },
    });

    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      // Spec §7 failure mapping: 401 / 404 / 500.
      if (body.error === "RefreshAuthFailed") {
        log(`[api/refresh failed] mode=${mode} http=${response.status} service=${body.serviceStatus} ${body.serviceBody ?? ""}`);
      } else if (body.error || body.message) {
        log(`[api/refresh failed] mode=${mode} http=${response.status} ${body.error ?? ""}: ${body.message ?? ""}`);
      } else {
        log(`[api/refresh failed] mode=${mode} http=${response.status}`);
      }
      return;
    }

    // Spec §2/§3 success shape — apply { accessToken, tokenLifetimeSeconds } so the next
    // accessTokenFactory call uses the refreshed token.
    applyToken({
      accessToken: body.accessToken,
      expiresIn: body.tokenLifetimeSeconds,
    });
    log(`[api/refresh ok] mode=${body.mode ?? mode} tokenLifetimeSeconds=${body.tokenLifetimeSeconds}; connection state still: ${connection.state}`);
  } catch (error) {
    log(`[api/refresh failed] mode=${mode} ${error}`);
  } finally {
    button.disabled = !connection || connection.state !== signalR.HubConnectionState.Connected;
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
  refreshAuthPersistentButton.disabled = !isConnected;
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
  refreshAuthPersistentButton.disabled = isBusy || !connected;
  sendToUserButton.disabled = isBusy || !connected;
  sendToSelfButton.disabled = isBusy || !connected;
  sendToAllButton.disabled = isBusy || !connected;
}

function log(message) {
  const time = new Date().toLocaleTimeString();
  logOutput.textContent += `[${time}] ${message}\n`;
  logOutput.scrollTop = logOutput.scrollHeight;
}
