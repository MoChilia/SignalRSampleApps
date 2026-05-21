import * as signalR from "@microsoft/signalr";
import { runTransportSample } from "./transport-runner.js";

export function runLongPollingSample(hubUrl) {
  return runTransportSample(
    hubUrl,
    "Long Polling",
    signalR.HttpTransportType.LongPolling
  );
}