import { runAutoTransportSample } from "./auto-client.js";
import { runLongPollingSample } from "./long-polling-client.js";
import { runServerSentEventsSample } from "./sse-client.js";
import { runWebSocketSample } from "./websocket-client.js";

const hubUrl = process.env.HUB_URL ?? "http://localhost:5080/hub";
const requestedTransport = process.argv[2] ?? "all";

const transports = [
  ["auto", "Auto", runAutoTransportSample],
  ["websocket", "WebSockets", runWebSocketSample],
  ["sse", "Server-Sent Events", runServerSentEventsSample],
  ["long-polling", "Long Polling", runLongPollingSample]
];

const selectedTransports = selectTransports(requestedTransport);

for (const [, transportName, runTransport] of selectedTransports) {
  try {
    await runTransport(hubUrl);
  } catch (error) {
    console.error(`[${transportName}] failed`, error);
  }
}

function selectTransports(input) {
  const transport = input.toLowerCase();

  if (transport === "all") {
    return transports;
  }

  const selected = transports.find(([key]) => key === transport);

  if (selected) {
    return [selected];
  }

  console.error(`Unknown transport: ${input}`);
  console.error("Use one of: auto, websocket, sse, long-polling, all");
  process.exit(1);
}