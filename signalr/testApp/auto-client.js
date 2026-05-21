import { runTransportSample } from "./transport-runner.js";

export function runAutoTransportSample(hubUrl) {
  return runTransportSample(hubUrl, "Auto", undefined);
}