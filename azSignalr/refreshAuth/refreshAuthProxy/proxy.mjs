import http from "node:http";

const options = parseArguments(process.argv.slice(2));
const listenUrl = new URL(options.get("listen") ?? "http://localhost:5121");
const appA = new URL(options.get("app-a") ?? "http://localhost:5122");
const appB = new URL(options.get("app-b") ?? "http://localhost:5123");

if (listenUrl.protocol !== "http:" || appA.protocol !== "http:" || appB.protocol !== "http:") {
  throw new Error("Only http:// URLs are supported by this local test proxy.");
}

const server = http.createServer((request, response) => {
  const requestUrl = new URL(request.url ?? "/", listenUrl);
  const isRefresh = requestUrl.pathname === "/hub/refresh";
  const target = isRefresh ? appB : appA;
  const destination = new URL(requestUrl.pathname + requestUrl.search, target);

  console.log(`${request.method} ${requestUrl.pathname}${requestUrl.search} -> ${isRefresh ? "B" : "A"} ${destination.href}`);

  const headers = { ...request.headers, host: destination.host };
  const proxyRequest = http.request(destination, {
    method: request.method,
    headers,
  }, proxyResponse => {
    response.writeHead(proxyResponse.statusCode ?? 502, proxyResponse.statusMessage, proxyResponse.headers);
    proxyResponse.pipe(response);
  });

  proxyRequest.on("error", error => {
    console.error(`Proxy request to ${destination.href} failed: ${error.message}`);
    if (!response.headersSent) {
      response.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
    }
    response.end("Bad Gateway");
  });

  request.on("aborted", () => proxyRequest.destroy());
  request.pipe(proxyRequest);
});

server.listen(Number(listenUrl.port || 80), listenUrl.hostname, () => {
  console.log(`Refresh routing proxy listening on ${listenUrl.href}`);
  console.log(`  app A: ${appA.href} (all requests except /hub/refresh)`);
  console.log(`  app B: ${appB.href} (/hub/refresh only)`);
});

function parseArguments(argumentsList) {
  const supported = new Set(["listen", "app-a", "app-b"]);
  const result = new Map();
  for (let index = 0; index < argumentsList.length; index++) {
    const argument = argumentsList[index];
    if (argument === "--help") {
      console.log("Usage: node proxy.mjs [--listen http://localhost:5121] [--app-a http://localhost:5122] [--app-b http://localhost:5123]");
      process.exit(0);
    }
    if (!argument.startsWith("--")) {
      throw new Error(`Unexpected positional argument '${argument}'.`);
    }

    const option = argument.slice(2);
    const separator = option.indexOf("=");
    const name = separator >= 0 ? option.slice(0, separator) : option;
    if (!supported.has(name)) {
      throw new Error(`Unknown argument '--${name}'.`);
    }
    if (result.has(name)) {
      throw new Error(`Argument '--${name}' was specified more than once.`);
    }

    let value;
    if (separator >= 0) {
      value = option.slice(separator + 1);
    } else if (++index < argumentsList.length && !argumentsList[index].startsWith("--")) {
      value = argumentsList[index];
    } else {
      throw new Error(`Argument '--${name}' requires a value.`);
    }
    result.set(name, value);
  }
  return result;
}
