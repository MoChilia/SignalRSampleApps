const userIdInput = document.querySelector("#userId");
const roleInput = document.querySelector("#role");
const hubUrlInput = document.querySelector("#hubUrl");
const targetUserIdInput = document.querySelector("#targetUserId");
const messageInput = document.querySelector("#message");
const connectButton = document.querySelector("#connect");
const disconnectButton = document.querySelector("#disconnect");
const sendToUserButton = document.querySelector("#sendToUser");
const sendToSelfButton = document.querySelector("#sendToSelf");
const sendToAllButton = document.querySelector("#sendToAll");
const stateText = document.querySelector("#state");
const connectionIdText = document.querySelector("#connectionId");
const tokenOutput = document.querySelector("#token");
const logOutput = document.querySelector("#log");

let connection;

connectButton.addEventListener("click", connect);
disconnectButton.addEventListener("click", disconnect);
sendToUserButton.addEventListener("click", sendToUser);
sendToSelfButton.addEventListener("click", sendToSelf);
sendToAllButton.addEventListener("click", sendToAll);

async function connect() {
  const userId = userIdInput.value.trim();
  const role = roleInput.value.trim();
  const hubUrl = hubUrlInput.value.trim();
  const token = await generateToken(hubUrl, userId, role);

  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      accessTokenFactory: () => token
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on("ReceiveMessage", (senderUserId, message) => {
    log(`[ReceiveMessage] from=${senderUserId}: ${message}`);
  });

  connection.onclose((error) => {
    setConnected(false);
    log(error ? `[closed] ${error}` : "[closed]");
  });

  try {
    setBusy(true);
    log(`[connect] user=${userId}, role=${role}`);
    await connection.start();
    setConnected(true);
    log(`[connected] connectionId=${connection.connectionId ?? "n/a"}`);
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

async function generateToken(hubUrl, userId, role) {
  const tokenUrl = new URL("/token", hubUrl).toString();
  const response = await fetch(tokenUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ userId, role })
  });

  if (!response.ok) {
    throw new Error(`Token request failed: ${response.status}`);
  }

  const tokenResponse = await response.json();
  tokenOutput.textContent = tokenResponse.accessToken;
  return tokenResponse.accessToken;
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
  sendToUserButton.disabled = !isConnected;
  sendToSelfButton.disabled = !isConnected;
  sendToAllButton.disabled = !isConnected;
}

function setBusy(isBusy) {
  connectButton.disabled = isBusy || connection?.state === signalR.HubConnectionState.Connected;
  disconnectButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
  sendToUserButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
  sendToSelfButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
  sendToAllButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
}

function log(message) {
  const time = new Date().toLocaleTimeString();
  logOutput.textContent += `[${time}] ${message}\n`;
  logOutput.scrollTop = logOutput.scrollHeight;
}