const targets = new Set(["self-hosted", "self", "azure", "asrs"]);
const requestedTarget = process.argv[2]?.toLowerCase();

if (!targets.has(requestedTarget)) {
  process.argv.splice(2, 0, "azure");
}

await import("../../shared/signalr-client/signalr-client.js");