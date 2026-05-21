import * as signalR from "@microsoft/signalr";
import EventSource from "eventsource";
import WebSocket from "ws";

const quietLogger = { log: () => {} };

export const transportLogger = {
  log(logLevel, message) {
    if (logLevel < signalR.LogLevel.Information) {
      return;
    }

    if (
      message.includes("WebSocket connected") ||
      message.includes("SSE connected") ||
      message.includes("LongPolling connected") ||
      message.includes("Starting transport")
    ) {
      console.log(`[signalr] ${message}`);
    }
  }
};

export function createNetworkTraceOptions(transportName) {
  const traceState = { stopping: false };

  return {
    httpClient: new NetworkTraceHttpClient(transportName, traceState),
    WebSocket: createTracedWebSocket(transportName),
    EventSource: createTracedEventSource(transportName),
    markStopping: () => {
      traceState.stopping = true;
    }
  };
}

class NetworkTraceHttpClient extends signalR.HttpClient {
  constructor(transportName, traceState) {
    super();
    this.transportName = transportName;
    this.traceState = traceState;
    this.innerClient = new signalR.DefaultHttpClient(quietLogger);
  }

  async send(request) {
    const method = request.method ?? "GET";
    const url = request.url ?? "";
    const requestBody = describeBody(request.content);

    console.log(
      `[${this.transportName}] --> ${method} ${describeUrl(url)}${requestBody}`
    );

    try {
      const response = await this.innerClient.send(request);
      console.log(
        `[${this.transportName}] <-- ${response.statusCode} ${method} ${describeUrl(url)}${describeResponse(response.content)}`
      );
      return response;
    } catch (error) {
      if (request.abortSignal?.aborted) {
        console.log(`[${this.transportName}] <-- aborted ${method} ${describeUrl(url)}`);
        throw error;
      }

      if (this.traceState.stopping) {
        console.log(`[${this.transportName}] <-- stopped ${method} ${describeUrl(url)}`);
        throw error;
      }

      console.log(`[${this.transportName}] <-- failed ${method} ${describeUrl(url)}`);
      throw error;
    }
  }

  getCookieString(url) {
    return this.innerClient.getCookieString(url);
  }
}

function createTracedWebSocket(transportName) {
  return class TracedWebSocket extends WebSocket {
    constructor(url, protocols, options) {
      console.log(`[${transportName}] --> WS ${describeUrl(url.toString())}`);
      super(url, protocols, options);

      this.on("open", () => {
        console.log(`[${transportName}] <-- WS open ${describeUrl(url.toString())}`);
      });
      this.on("close", (code) => {
        console.log(`[${transportName}] <-- WS close ${code}`);
      });
    }
  };
}

function createTracedEventSource(transportName) {
  return class TracedEventSource extends EventSource {
    constructor(url, options) {
      console.log(`[${transportName}] --> GET ${describeUrl(url.toString())} body=<empty>`);
      super(url, options);
    }

    set onmessage(listener) {
      super.onmessage = (event) => {
        console.log(`[${transportName}] <-- SSE event data=${formatContent(event.data)}`);
        listener?.(event);
      };
    }

    get onmessage() {
      return super.onmessage;
    }
  };
}

function describeUrl(rawUrl) {
  if (!rawUrl) {
    return "<unknown url>";
  }

  const url = new URL(rawUrl);
  return `${url.pathname}${url.search}`;
}

function describeBody(content) {
  if (!content) {
    return " body=<empty>";
  }

  return ` body=${formatContent(content)}`;
}

function describeResponse(content) {
  if (!content) {
    return "";
  }

  if (typeof content !== "string") {
    return ` body=${formatContent(content)}`;
  }

  try {
    const json = JSON.parse(content);

    if (json.connectionToken && json.availableTransports) {
      const transports = json.availableTransports
        .map((transport) => transport.transport)
        .join(", ");

      return ` negotiateResponse={ connectionToken: "${json.connectionToken}", availableTransports: [${transports}] }`;
    }
  } catch {
    return ` body=${formatContent(content)}`;
  }

  return ` body=${formatContent(content)}`;
}

function formatContent(content) {
  if (typeof content !== "string") {
    return `<${content.byteLength} binary bytes>`;
  }

  const visibleContent = content.replaceAll("\u001e", "<RS>");
  return JSON.stringify(visibleContent);
}