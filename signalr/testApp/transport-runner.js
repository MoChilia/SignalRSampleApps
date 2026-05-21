import * as signalR from "@microsoft/signalr";
import { createNetworkTraceOptions, transportLogger } from "./network-trace.js";

export async function runTransportSample(hubUrl, transportName, transport) {
  const traceOptions = createNetworkTraceOptions(transportName);
  const { markStopping, ...connectionTraceOptions } = traceOptions;
  const connectionOptions = transport === undefined
    ? connectionTraceOptions
    : { transport, ...connectionTraceOptions };

  const connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, connectionOptions)
    .configureLogging(transportLogger)
    .build();

  connection.on("messageReceived", (user, message) => {
    console.log(`[${transportName}] received from ${user}: ${message}`);
  });

  try {
    console.log(`[${transportName}] negotiating with ${hubUrl}`);
    await connection.start();
    console.log(`[${transportName}] connected`);

    await connection.invoke(
      "NewMessage",
      Date.now(),
      `hello from ${transportName}`
    );
  } finally {
    markStopping();
    await connection.stop();
    console.log(`[${transportName}] stopped`);
  }
}