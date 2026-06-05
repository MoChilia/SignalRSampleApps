const userIdInput = document.querySelector("#userId");
const roleInput = document.querySelector("#role");
const hubUrlInput = document.querySelector("#hubUrl");
const extendSecondsInput = document.querySelector("#extendSeconds");
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
let currentToken = null;
let currentExpiresAt = 0; // unix seconds
let countdownTimer = null;

connectButton.addEventListener("click", connect);
disconnectButton.addEventListener("click", disconnect);
refreshAuthRestButton.addEventListener("click", refreshAuthViaRest);
sendToUserButton.addEventListener("click", sendToUser);
sendToSelfButton.addEventListener("click", sendToSelf);
sendToAllButton.addEventListener("click", sendToAll);

async function connect() {
  const userId = userIdInput.value.trim();
  const role = roleInput.value.trim();
  const hubUrl = hubUrlInput.value.trim();

  await fetchInitialToken(hubUrl, userId, role);

  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      // Always hand SignalR the freshest token we have. On (re)connect this lets the new
      // exp flow into the service negotiate; for an existing WebSocket the service runtime
      // is responsible for honoring a pushed RefreshAuthMessage (Phase 1 of the service runtime).
      accessTokenFactory: () => currentToken
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on("ReceiveMessage", (senderUserId, message) => {
    log(`[ReceiveMessage] from=${senderUserId}: ${message}`);
  });

  connection.onclose((error) => {
    setConnected(false);
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
//   POST /api/refresh?hub={hub}&id={connectionIdOrToken}
//   Authorization: Bearer {app-token}
//   200 OK { accessToken, tokenLifetimeSeconds }
// The ASRS runtime advances CloseOnAuthExpirationFeature.ExpiresOn in place; the browser
// keeps the same WebSocket and gets a refreshed accessToken for the next (re)connect.
async function refreshAuthViaRest() {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  const hubUrl = hubUrlInput.value.trim();
  const extendSeconds = Number(extendSecondsInput.value) || 60;
  const connectionId = connection.connectionId;

  refreshAuthRestButton.disabled = true;
  try {
    log(`[api/refresh] POST id=${connectionId} +${extendSeconds}s`);
    const url = new URL(
      `/api/refresh?hub=ChatHub&id=${encodeURIComponent(connectionId)}&additionalSeconds=${extendSeconds}`,
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
        log(`[api/refresh failed] http=${response.status} service=${body.serviceStatus} ${body.serviceBody ?? ""}`);
      } else if (body.error || body.message) {
        log(`[api/refresh failed] http=${response.status} ${body.error ?? ""}: ${body.message ?? ""}`);
      } else {
        log(`[api/refresh failed] http=${response.status}`);
      }
      return;
    }

    // Spec §2/§3 success shape — apply { accessToken, tokenLifetimeSeconds } so the next
    // accessTokenFactory call uses the refreshed token.
    applyToken({
      accessToken: body.accessToken,
      expiresIn: body.tokenLifetimeSeconds,
    });
    log(`[api/refresh ok] tokenLifetimeSeconds=${body.tokenLifetimeSeconds}; connection state still: ${connection.state}`);
  } catch (error) {
    log(`[api/refresh failed] ${error}`);
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

function setConnected(isConnected) {
  stateText.textContent = isConnected ? `Connected as ${userIdInput.value.trim()}` : "Disconnected";
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
