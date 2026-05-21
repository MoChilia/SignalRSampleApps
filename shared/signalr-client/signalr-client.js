import { runAutoTransportSample } from "./auto-client.js";
import { runLongPollingSample } from "./long-polling-client.js";
import { runServerSentEventsSample } from "./sse-client.js";
import { runWebSocketSample } from "./websocket-client.js";

const targets = new Map([
  ["self-hosted", { label: "self-hosted SignalR", hubUrl: "http://localhost:5080/hub" }],
  ["self", { label: "self-hosted SignalR", hubUrl: "http://localhost:5080/hub" }],
  ["azure", { label: "Azure SignalR Service", hubUrl: "http://localhost:5090/hub" }],
  ["asrs", { label: "Azure SignalR Service", hubUrl: "http://localhost:5090/hub" }]
]);

const transports = [
  ["auto", "Auto", runAutoTransportSample],
  ["websocket", "WebSockets", runWebSocketSample],
  ["sse", "Server-Sent Events", runServerSentEventsSample],
  ["long-polling", "Long Polling", runLongPollingSample]
];

const { target, requestedTransport } = parseArguments(process.argv.slice(2));
const hubUrl = process.env.HUB_URL ?? target.hubUrl;
const selectedTransports = selectTransports(requestedTransport);

console.log(`[client] target=${target.label}`);
console.log(`[client] hub=${hubUrl}`);

for (const [, transportName, runTransport] of selectedTransports) {
  try {
    await runTransport(hubUrl);
  } catch (error) {
    console.error(`[${transportName}] failed`, error);
  }
}

function parseArguments(args) {
  const [firstArg, secondArg] = args;
  const target = targets.get((firstArg ?? "self-hosted").toLowerCase());

  if (target) {
    return {
      target,
      requestedTransport: secondArg ?? "all"
    };
  }

  return {
    target: targets.get("self-hosted"),
    requestedTransport: firstArg ?? "all"
  };
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
  console.error("Use: npm start -- [self-hosted|azure|asrs] [auto|websocket|sse|long-polling|all]");
  process.exit(1);
}