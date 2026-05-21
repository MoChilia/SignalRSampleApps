import * as signalR from "@microsoft/signalr";
import { runTransportSample } from "./transport-runner.js";

export function runServerSentEventsSample(hubUrl) {
  return runTransportSample(
    hubUrl,
    "Server-Sent Events",
    signalR.HttpTransportType.ServerSentEvents
  );
}