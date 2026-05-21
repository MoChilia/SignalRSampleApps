const transportTypes = {
  websocket: signalR.HttpTransportType.WebSockets,
  sse: signalR.HttpTransportType.ServerSentEvents,
  "long-polling": signalR.HttpTransportType.LongPolling
};

const hubUrlInput = document.querySelector("#hubUrl");
const transportSelect = document.querySelector("#transport");
const connectButton = document.querySelector("#connect");
const disconnectButton = document.querySelector("#disconnect");
const sendButton = document.querySelector("#send");
const messageInput = document.querySelector("#message");
const stateText = document.querySelector("#state");
const connectionIdText = document.querySelector("#connectionId");
const logOutput = document.querySelector("#log");

let connection;

hubUrlInput.value = `${window.location.origin}/hub`;

connectButton.addEventListener("click", connect);
disconnectButton.addEventListener("click", disconnect);
sendButton.addEventListener("click", sendMessage);
messageInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !sendButton.disabled) {
    sendMessage();
  }
});

async function connect() {
  const hubUrl = hubUrlInput.value.trim();
  const selectedTransport = transportSelect.value;
  const connectionOptions = selectedTransport === "auto"
    ? {}
    : { transport: transportTypes[selectedTransport] };

  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, connectionOptions)
    .configureLogging(signalR.LogLevel.Information)
    .withAutomaticReconnect()
    .build();

  connection.on("messageReceived", (user, message) => {
    log(`[messageReceived] ${user}: ${message}`);
  });

  connection.onreconnecting((error) => {
    stateText.textContent = "Reconnecting";
    log(error ? `[reconnecting] ${error}` : "[reconnecting]");
  });

  connection.onreconnected((connectionId) => {
    setConnected(true);
    log(`[reconnected] connectionId=${connectionId ?? "n/a"}`);
  });

  connection.onclose((error) => {
    setConnected(false);
    log(error ? `[closed] ${error}` : "[closed]");
  });

  try {
    setBusy(true);
    log(`[connect] ${selectedTransport} -> ${hubUrl}`);
    log("[negotiate] POST /hub/negotiate?negotiateVersion=1");
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

async function sendMessage() {
  const message = messageInput.value.trim();

  if (!message || !connection) {
    return;
  }

  log(`[send] NewMessage(${Date.now()}, ${JSON.stringify(message)})`);
  await connection.invoke("NewMessage", Date.now(), message);
}

function setConnected(isConnected) {
  stateText.textContent = isConnected ? "Connected" : "Disconnected";
  connectionIdText.textContent = isConnected && connection?.connectionId
    ? `connectionId=${connection.connectionId}`
    : "";
  connectButton.disabled = isConnected;
  disconnectButton.disabled = !isConnected;
  sendButton.disabled = !isConnected;
  hubUrlInput.disabled = isConnected;
  transportSelect.disabled = isConnected;
}

function setBusy(isBusy) {
  connectButton.disabled = isBusy || connection?.state === signalR.HubConnectionState.Connected;
  disconnectButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
  sendButton.disabled = isBusy || connection?.state !== signalR.HubConnectionState.Connected;
}

function log(message) {
  const time = new Date().toLocaleTimeString();
  logOutput.textContent += `[${time}] ${message}\n`;
  logOutput.scrollTop = logOutput.scrollHeight;
}