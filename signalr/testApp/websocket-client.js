import * as signalR from "@microsoft/signalr";
import { runTransportSample } from "./transport-runner.js";

export function runWebSocketSample(hubUrl) {
  return runTransportSample(
    hubUrl,
    "WebSockets",
    signalR.HttpTransportType.WebSockets
  );
}