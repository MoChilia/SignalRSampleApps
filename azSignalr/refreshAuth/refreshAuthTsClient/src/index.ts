import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { createInterface } from "node:readline/promises";
import { stdin, stdout } from "node:process";

interface TokenResponse {
    accessToken?: string;
    expiresIn?: number;
}

interface Options {
    serverUrl: string;
    userId: string;
    role: string;
    transport: HttpTransportType;
    autoRefresh: boolean;
    interactive: boolean;
    lifetimeSeconds: number;
    refreshBeforeExpirationInMilliseconds: number;
    expectedAutoRefreshes: number;
    autoRefreshTimeoutInMilliseconds: number;
}

interface JwtPayload {
    exp?: number;
    iat?: number;
    [claim: string]: unknown;
}

function parseOptions(args: string[]): Options {
    const values = new Map<string, string>();
    for (let index = 0; index < args.length; index += 2) {
        const name = args[index];
        const value = args[index + 1];
        if (!name?.startsWith("--") || !value) {
            throw new Error(`Expected --name value arguments, received '${args.join(" ")}'.`);
        }
        values.set(name.substring(2), value);
    }

    const transportName = (values.get("transport") ?? "ws").toLowerCase();
    const transport = transportName === "ws" || transportName === "websockets"
        ? HttpTransportType.WebSockets
        : transportName === "sse"
            ? HttpTransportType.ServerSentEvents
            : transportName === "lp" || transportName === "longpolling"
                ? HttpTransportType.LongPolling
                : undefined;
    if (!transport) {
        throw new Error("transport must be ws, sse, or lp.");
    }

    const lifetimeSeconds = Number.parseInt(values.get("lifetime-seconds") ?? "60", 10);
    const refreshBeforeExpirationInMilliseconds = Number.parseInt(values.get("refresh-before-ms") ?? "20000", 10);
    const expectedAutoRefreshes = Number.parseInt(values.get("expected-auto-refreshes") ?? "0", 10);
    const autoRefreshTimeoutInMilliseconds = Number.parseInt(values.get("auto-refresh-timeout-ms") ?? "60000", 10);
    if (!Number.isInteger(lifetimeSeconds) || lifetimeSeconds <= 0) {
        throw new Error("lifetime-seconds must be a positive integer.");
    }
    if (!Number.isInteger(refreshBeforeExpirationInMilliseconds) || refreshBeforeExpirationInMilliseconds < 0) {
        throw new Error("refresh-before-ms must be a non-negative integer.");
    }
    if (!Number.isInteger(expectedAutoRefreshes) || expectedAutoRefreshes < 0) {
        throw new Error("expected-auto-refreshes must be a non-negative integer.");
    }
    if (!Number.isInteger(autoRefreshTimeoutInMilliseconds) || autoRefreshTimeoutInMilliseconds <= 0) {
        throw new Error("auto-refresh-timeout-ms must be a positive integer.");
    }

    return {
        serverUrl: (values.get("server-url") ?? "http://localhost:5121").replace(/\/$/, ""),
        userId: values.get("user-id") ?? "alice",
        role: values.get("role") ?? "user",
        transport,
        autoRefresh: parseSwitch(values.get("auto-refresh") ?? "off", "auto-refresh"),
        interactive: parseSwitch(values.get("interactive") ?? "off", "interactive"),
        lifetimeSeconds,
        refreshBeforeExpirationInMilliseconds,
        expectedAutoRefreshes,
        autoRefreshTimeoutInMilliseconds,
    };
}

function parseSwitch(value: string, name: string): boolean {
    if (value === "on" || value === "true") {
        return true;
    }
    if (value === "off" || value === "false") {
        return false;
    }
    throw new Error(`${name} must be on or off.`);
}

async function getAppToken(options: Options): Promise<string> {
    const response = await fetch(`${options.serverUrl}/token`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
            userId: options.userId,
            role: options.role,
            lifetimeSeconds: options.lifetimeSeconds,
            includeExpiration: true,
            scenario: "standard",
        }),
    });
    if (!response.ok) {
        throw new Error(`Token endpoint returned ${response.status}.`);
    }

    const token = await response.json() as TokenResponse;
    if (!token.accessToken) {
        throw new Error("Token endpoint response did not contain accessToken.");
    }
    console.log(`[token] minted app token (expiresIn=${token.expiresIn ?? "none"}s)`);
    return token.accessToken;
}

function getServiceAccessToken(connection: unknown): string | undefined {
    return (connection as any).connection?._httpClient?._accessToken;
}

function decodeJwtPayload(token: string | undefined): JwtPayload | undefined {
    if (!token) {
        return undefined;
    }
    const payload = token.split(".")[1];
    if (!payload) {
        return undefined;
    }
    return JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as JwtPayload;
}

function printServiceToken(label: string, token: string | undefined): void {
    const payload = decodeJwtPayload(token);
    console.log(`[service-token:${label}] present=${!!token}; iat=${payload?.iat ?? "missing"}; exp=${payload?.exp ?? "missing"}`);
}

function delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function main(): Promise<void> {
    const options = parseOptions(process.argv.slice(2));
    const connection = new HubConnectionBuilder()
        .withUrl(`${options.serverUrl}/hub`, {
            transport: options.transport,
            accessTokenFactory: () => getAppToken(options),
        })
        .configureLogging(LogLevel.Warning)
        .withAuthenticationRefresh({
            enableAutoRefresh: options.autoRefresh,
            refreshBeforeExpirationInMilliseconds: options.refreshBeforeExpirationInMilliseconds,
        })
        .build();

    connection.on("ReceiveMessage", () => undefined);
    let lastServiceToken: string | undefined;
    let automaticRefreshCount = 0;
    let completeAutomaticRefreshWait: (() => void) | undefined;
    const automaticRefreshWait = new Promise<void>(resolve => completeAutomaticRefreshWait = resolve);
    connection.onAuthenticationRefreshed(context => {
        const serviceToken = getServiceAccessToken(connection);
        const changed = !!lastServiceToken && serviceToken !== lastServiceToken;
        const payload = decodeJwtPayload(serviceToken);
        console.log(`[refresh:event] at=${context.refreshedAt.toISOString()}; lifetime=${context.newTokenLifetimeInSeconds ?? "omitted"}; serviceTokenChanged=${changed}; serviceTokenExp=${payload?.exp ?? "missing"}`);
        lastServiceToken = serviceToken;
        automaticRefreshCount++;
        if (automaticRefreshCount >= options.expectedAutoRefreshes) {
            completeAutomaticRefreshWait?.();
        }
    });
    connection.onAuthenticationRefreshFailed(context => {
        console.error(`[refresh:failed] ${context.error.message}`);
    });

    try {
        await connection.start();
        const connectionId = connection.connectionId;
        if (!connectionId) {
            throw new Error("Connection did not expose a connection ID.");
        }
        console.log(`[conn] connected: ${connectionId}`);
        lastServiceToken = getServiceAccessToken(connection);
        printServiceToken("initial", lastServiceToken);

        if (options.expectedAutoRefreshes > 0) {
            if (!options.autoRefresh) {
                throw new Error("expected-auto-refreshes requires --auto-refresh on.");
            }
            const initialMarker = await connection.invoke<string>("WhoAmIMarker");
            const initialToken = lastServiceToken;
            const initialTokenExpiration = decodeJwtPayload(initialToken)?.exp;
            let timeoutHandle: ReturnType<typeof setTimeout> | undefined;
            const timeout = new Promise<never>((_, reject) => {
                timeoutHandle = setTimeout(
                    () => reject(new Error(`Timed out waiting for ${options.expectedAutoRefreshes} automatic refresh event(s); observed ${automaticRefreshCount}.`)),
                    options.autoRefreshTimeoutInMilliseconds);
            });
            try {
                await Promise.race([automaticRefreshWait, timeout]);
            } finally {
                if (timeoutHandle) {
                    clearTimeout(timeoutHandle);
                }
            }
            await delay(500);
            const refreshedMarker = await connection.invoke<string>("WhoAmIMarker");
            const refreshedToken = getServiceAccessToken(connection);
            const refreshedTokenExpiration = decodeJwtPayload(refreshedToken)?.exp;
            if (connection.connectionId !== connectionId) {
                throw new Error("Automatic refresh unexpectedly reconnected the client.");
            }
            if (initialMarker === refreshedMarker) {
                throw new Error("Automatic refresh did not apply the refreshed marker claim.");
            }
            if (!initialToken || !refreshedToken || initialToken === refreshedToken) {
                throw new Error("Automatic refresh did not replace the cached service access token.");
            }
            if (initialTokenExpiration === undefined || refreshedTokenExpiration === undefined || refreshedTokenExpiration <= initialTokenExpiration) {
                throw new Error(`Automatic refresh did not advance service-token expiration (before=${initialTokenExpiration}, after=${refreshedTokenExpiration}).`);
            }
            console.log(`[pass] TypeScript automatic refresh ran ${automaticRefreshCount} time(s), retained connection ${connectionId}, and adopted the refreshed service token.`);
            return;
        }

        if (options.interactive) {
            console.log(`[client] interactive mode; autoRefresh=${options.autoRefresh}; lifetime=${options.lifetimeSeconds}s; refreshBefore=${options.refreshBeforeExpirationInMilliseconds}ms`);
            await runInteractive(connection, options, connectionId);
            return;
        }

        const beforeMarker = await connection.invoke<string>("WhoAmIMarker");
        const beforeExp = await connection.invoke<number>("WhoAmIExp");
        const beforeServiceToken = getServiceAccessToken(connection);
        const beforeServiceTokenExp = decodeJwtPayload(beforeServiceToken)?.exp;

        // Ensure freshly minted app/service tokens have a measurably later whole-second expiration.
        await delay(1100);

        const refreshedLifetime = await connection.refreshAuthentication();

        const afterMarker = await connection.invoke<string>("WhoAmIMarker");
        const afterExp = await connection.invoke<number>("WhoAmIExp");

        if (beforeMarker === afterMarker) {
            throw new Error("Refresh did not apply the refreshed marker claim.");
        }
        if (afterExp <= beforeExp) {
            throw new Error(`Refresh did not advance the service access token expiration (beforeExp=${beforeExp}, afterExp=${afterExp}).`);
        }
        if (connection.connectionId !== connectionId) {
            throw new Error("Refresh unexpectedly reconnected the client.");
        }

        const currentAccessToken = getServiceAccessToken(connection);
        const currentServiceTokenExp = decodeJwtPayload(currentAccessToken)?.exp;
        if (!beforeServiceToken || !currentAccessToken || currentAccessToken === beforeServiceToken) {
            throw new Error("Client did not replace the cached service access token.");
        }
        if (beforeServiceTokenExp === undefined || currentServiceTokenExp === undefined || currentServiceTokenExp <= beforeServiceTokenExp) {
            throw new Error(`Service access token expiration did not advance (before=${beforeServiceTokenExp}, after=${currentServiceTokenExp}).`);
        }

        printServiceToken("refreshed", currentAccessToken);
        console.log(`[refresh] succeeded; new lifetime=${refreshedLifetime ?? "omitted"}; exp advanced from ${beforeExp} to ${afterExp}; marker changed`);
        console.log(`[pass] TypeScript refresh retained connection ${connectionId} and adopted the refreshed service token.`);
    } finally {
        await connection.stop();
    }
}

async function runInteractive(connection: any, options: Options, initialConnectionId: string): Promise<void> {
    const input = createInterface({ input: stdin, output: stdout });
    console.log("Commands: /refresh, /marker, /status, /help, /quit");
    try {
        while (true) {
            const command = (await input.question("> ")).trim();
            if (command === "/quit" || command === "") {
                return;
            }
            if (command === "/refresh") {
                const before = getServiceAccessToken(connection);
                const lifetime = await connection.refreshAuthentication();
                const after = getServiceAccessToken(connection);
                console.log(`[refresh:manual] lifetime=${lifetime ?? "omitted"}; serviceTokenChanged=${!!before && after !== before}`);
                continue;
            }
            if (command === "/marker") {
                console.log(`[marker] ${await connection.invoke("WhoAmIMarker")}`);
                continue;
            }
            if (command === "/status") {
                const token = getServiceAccessToken(connection);
                const payload = decodeJwtPayload(token);
                const marker = await connection.invoke("WhoAmIMarker");
                const appExp = await connection.invoke("WhoAmIExp");
                console.log(`[status] connectionId=${connection.connectionId}; unchanged=${connection.connectionId === initialConnectionId}; marker=${marker}; appExp=${appExp}; serviceTokenExp=${payload?.exp ?? "missing"}; autoRefresh=${options.autoRefresh}`);
                continue;
            }
            if (command === "/help") {
                console.log("Commands: /refresh, /marker, /status, /help, /quit");
                continue;
            }
            console.log(`Unknown command '${command}'. Use /help.`);
        }
    } finally {
        input.close();
    }
}

main().catch(error => {
    console.error(`[fail] ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
    process.exitCode = 1;
});