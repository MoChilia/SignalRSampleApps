using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// Minimal .NET client for the refreshAuthApp server. Exercises the locally-built aspnetcore
// SignalR client's Default-mode auth refresh (WithAuthenticationRefresh + {hubUrl}/refresh).
//
// Usage: dotnet run -- [--server-url URL] [--user-id USER] [--role ROLE] [--transport TRANSPORT]
//   [--auto-refresh on|off] [--auth-mode MODE] [--lifetime-seconds SECONDS]
//   [--expiration on|off] [--scenario SCENARIO] [--auto-reconnect on|off] [--negotiate-version 0|1]
//   [--ignore-refreshed-access-token on|off] [--reconnect-delay-seconds SECONDS]
//   [--refresh-before-seconds SECONDS] [--endpoint-name NAME]
//   transport    = ws | sse | lp | all   (default ws)
//   autorefresh  = on | off               (default on)
//   authmode     = token | cookie | none  (default token)
//   expiration   = on | off               (default on; token mode only)
//   autoreconnect = on | off              (default on)
//   negotiateVersion = 0 | 1              (default client behavior when omitted)
//   e.g. dotnet run http://localhost:5121 alice user sse off cookie

Dictionary<string, string>? namedArguments;
try
{
    namedArguments = ParseNamedArguments(args);
}
catch (ArgumentException ex)
{
    Console.WriteLine(ex.Message);
    Console.WriteLine("Run with --help to see supported arguments.");
    return;
}

if (namedArguments?.ContainsKey("help") == true)
{
    PrintUsage();
    return;
}

string? GetArgument(string name, int position, string? defaultValue = null)
{
    if (namedArguments is null)
    {
        return args.Length > position ? args[position] : defaultValue;
    }
    return namedArguments.TryGetValue(name, out var value) ? value : defaultValue;
}

var serverBaseUrl = GetArgument("server-url", 0, "http://localhost:5121")!.TrimEnd('/');
var userId = GetArgument("user-id", 1, "alice")!;
var role = GetArgument("role", 2, "user")!;
var transportArg = GetArgument("transport", 3, "ws")!.ToLowerInvariant();
var autoRefresh = !IsOff(GetArgument("auto-refresh", 4, "on")!);
var authMode = GetArgument("auth-mode", 5, "token")!.ToLowerInvariant();
var lifetimeArgument = GetArgument("lifetime-seconds", 6);
var configuredLifetimeSeconds = lifetimeArgument is not null && int.TryParse(lifetimeArgument, out var lifetimeSeconds)
    ? lifetimeSeconds
    : (int?)null;
if (lifetimeArgument is not null && configuredLifetimeSeconds is null)
{
    Console.WriteLine("lifetime-seconds must be an integer.");
    return;
}
var includeExpiration = !IsOff(GetArgument("expiration", 7, "on")!);
var scenario = GetArgument("scenario", 8, "standard")!.ToLowerInvariant();
var autoReconnect = !IsOff(GetArgument("auto-reconnect", 9, "on")!);
var negotiateVersionArgument = GetArgument("negotiate-version", 10);
var ignoreRefreshedAccessToken = !IsOff(GetArgument("ignore-refreshed-access-token", 11, "off")!);
var reconnectDelayArgument = GetArgument("reconnect-delay-seconds", 12);
var reconnectDelaySeconds = reconnectDelayArgument is not null
    && int.TryParse(reconnectDelayArgument, out var parsedReconnectDelaySeconds)
    && parsedReconnectDelaySeconds >= 0
        ? parsedReconnectDelaySeconds
        : (int?)null;
if (reconnectDelayArgument is not null && reconnectDelaySeconds is null)
{
    Console.WriteLine("reconnect-delay-seconds must be a non-negative integer.");
    return;
}
var refreshBeforeArgument = GetArgument("refresh-before-seconds", 13);
var configuredRefreshBeforeSeconds = refreshBeforeArgument is not null
    && int.TryParse(refreshBeforeArgument, out var parsedRefreshBeforeSeconds)
    && parsedRefreshBeforeSeconds >= 0
        ? parsedRefreshBeforeSeconds
        : (int?)null;
if (refreshBeforeArgument is not null && configuredRefreshBeforeSeconds is null)
{
    Console.WriteLine("refresh-before-seconds must be a non-negative integer.");
    return;
}
var refreshBeforeSeconds = configuredRefreshBeforeSeconds ?? 20;
var endpointName = GetArgument("endpoint-name", 14);
var negotiateVersion = negotiateVersionArgument is not null && int.TryParse(negotiateVersionArgument, out var parsedNegotiateVersion)
    && parsedNegotiateVersion is 0 or 1
        ? parsedNegotiateVersion
        : (int?)null;
if (negotiateVersionArgument is not null && negotiateVersion is null)
{
    Console.WriteLine("negotiateVersion must be 0 or 1.");
    return;
}
var hubQuery = new List<string>();
if (negotiateVersion.HasValue)
{
    hubQuery.Add($"negotiateVersion={negotiateVersion}");
}
if (!string.IsNullOrWhiteSpace(endpointName))
{
    hubQuery.Add($"endpoint={Uri.EscapeDataString(endpointName)}");
}
var hubUrl = $"{serverBaseUrl}/hub" + (hubQuery.Count > 0 ? $"?{string.Join('&', hubQuery)}" : string.Empty);
var refreshUserId = userId;
var refreshRole = role;
var refreshLifetimeSeconds = configuredLifetimeSeconds;
var refreshIncludesExpiration = includeExpiration;
var useInvalidToken = false;
var queuedRefreshTokens = new ConcurrentQueue<RefreshTokenRequest>();
DateTimeOffset? connectedAt = null;

var transports = transportArg switch
{
    "sse" => HttpTransportType.ServerSentEvents,
    "lp" or "longpolling" => HttpTransportType.LongPolling,
    "ws" or "websockets" => HttpTransportType.WebSockets,
    "all" => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
    _ => HttpTransportType.WebSockets,
};

var cookies = new System.Net.CookieContainer();
using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies });

// App-plane token factory. The Azure SignalR Default-mode refresh posts to {hubUrl}/refresh using
// THIS provider (a fresh app token minted from the sample's /token endpoint), while transport
// requests use the service token captured at the negotiate redirect. Minting a fresh token on each
// call is what lets /refresh present a still-valid app token to advance the connection's lifetime.
async Task<string?> GetAppTokenAsync()
{
    if (useInvalidToken)
    {
        Console.WriteLine("[token] using intentionally invalid app token");
        return "invalid-app-token";
    }

    var request = queuedRefreshTokens.TryDequeue(out var queued)
        ? queued
        : new RefreshTokenRequest("default", refreshUserId, refreshRole, refreshLifetimeSeconds, refreshIncludesExpiration);
    var resp = await http.PostAsJsonAsync($"{serverBaseUrl}/token", new
    {
        userId = request.UserId,
        role = request.Role,
        lifetimeSeconds = request.LifetimeSeconds,
        includeExpiration = request.IncludeExpiration,
        scenario,
    });
    resp.EnsureSuccessStatusCode();
    var payload = await resp.Content.ReadFromJsonAsync<TokenResponse>();
    Console.WriteLine(
        $"[token:{request.Label}] minted app token for {request.UserId}/{request.Role} " +
        $"(expiresIn={payload?.ExpiresIn?.ToString() ?? "none"}s; expiresAt={payload?.ExpiresAt?.ToString() ?? "none"})");
    return payload?.AccessToken;
}

// Cookie-auth login: obtains the auth cookie into the shared CookieContainer (used only in cookie mode).
async Task LoginAsync()
{
    var resp = await http.PostAsJsonAsync($"{serverBaseUrl}/login", new { userId, role });
    resp.EnsureSuccessStatusCode();
    Console.WriteLine("[login] auth cookie issued");
}

if (authMode == "cookie")
{
    await LoginAsync();
}

var responseInspector = new ResponseInspector();
var completedAuthenticationRefreshes = 0;
var connectionBuilder = new HubConnectionBuilder()
    .WithUrl(hubUrl, options =>
    {
        options.Transports = transports;
        options.HttpMessageHandlerFactory = innerHandler =>
            new SignalRResponseInspectorHandler(responseInspector, ignoreRefreshedAccessToken) { InnerHandler = innerHandler };
        if (authMode == "cookie")
        {
            // Cookie auth: reuse the auth cookie from /login for negotiate/refresh. Sliding expiration
            // re-issues it on refresh (captured back into the same container), advancing the deadline.
            options.Cookies = cookies;
        }
        else if (authMode == "token")
        {
            // Token/bearer: mint a fresh app token for negotiate and each {hubUrl}/refresh.
            options.AccessTokenProvider = GetAppTokenAsync;
        }
    })
    .WithAuthenticationRefresh(o =>
    {
        // Toggle via the 5th arg. When off, no refresh timer is scheduled. Manual
        // RefreshAuthenticationAsync ('/refresh') still works regardless.
        o.EnableAutoRefresh = autoRefresh;
        o.RefreshBeforeExpiration = TimeSpan.FromSeconds(refreshBeforeSeconds);
        o.OnAuthenticationRefreshed = ctx =>
        {
            Interlocked.Increment(ref completedAuthenticationRefreshes);
            Console.WriteLine($"[refresh] succeeded at {ctx.RefreshedAt:HH:mm:ss}; new lifetime={ctx.NewTokenLifetime}");
            return Task.CompletedTask;
        };
        o.OnAuthenticationRefreshFailed = ctx =>
        {
            Console.WriteLine($"[refresh] FAILED: {ctx.Exception.Message}");
            return Task.CompletedTask;
        };
    });

if (autoReconnect)
{
    if (reconnectDelaySeconds is { } delaySeconds)
    {
        var delay = TimeSpan.FromSeconds(delaySeconds);
        connectionBuilder.WithAutomaticReconnect([delay, delay, delay, delay]);
    }
    else
    {
        connectionBuilder.WithAutomaticReconnect();
    }
}

var connection = connectionBuilder.Build();

connection.On<string, string>("ReceiveMessage", (user, message) =>
    Console.WriteLine($"[recv] {user}: {message}"));

// Continuous server->client stream. The monotonic sequence number lets us detect and report exactly
// which ticks were dropped: with auth refresh the stream is gapless; without it, messages the server
// sent during the abort+reconnect window are lost (SignalR is at-most-once with no buffering by default).
var lastTick = 0;
var totalLost = 0;
connection.On<int, string>("Tick", (seq, ts) =>
{
    if (lastTick != 0 && seq > lastTick + 1)
    {
        var missed = seq - lastTick - 1;
        totalLost += missed;
        Console.WriteLine(
            $"[LOST] {missed} tick(s) dropped (#{lastTick + 1}..#{seq - 1}) during the reconnect gap; total lost={totalLost}");
    }
    lastTick = seq;
    Console.WriteLine($"[tick] #{seq} @ {ts}");
});

connection.Closed += error =>
{
    var closedAt = DateTimeOffset.UtcNow;
    var elapsed = connectedAt.HasValue ? closedAt - connectedAt.Value : (TimeSpan?)null;
    Console.WriteLine(
        $"[conn] closed at {closedAt:HH:mm:ss.fff}Z after {elapsed?.ToString() ?? "unknown"}: " +
        $"{error?.Message ?? "(no error)"}");
    return Task.CompletedTask;
};

// Fires when the transport drops (e.g. close-on-auth abort at token expiry) and WithAutomaticReconnect
// starts recovering. With auth refresh ON these never fire (the deadline is advanced in place); with
// refresh OFF you'll see them every ~60s, and the connectionId changes on reconnect.
connection.Reconnecting += error =>
{
    Console.WriteLine($"[conn] RECONNECTING (connection dropped): {error?.Message ?? "(no error)"}");
    return Task.CompletedTask;
};

connection.Reconnected += connectionId =>
{
    Console.WriteLine($"[conn] RECONNECTED with new connectionId={connectionId}");
    return Task.CompletedTask;
};

var reconnectDelayDescription = reconnectDelaySeconds is { } configuredReconnectDelay
    ? $"{configuredReconnectDelay}s"
    : "default";
Console.WriteLine($"Connecting to {hubUrl} as '{userId}' ({role}) over {transports}; autoRefresh={autoRefresh}; autoReconnect={autoReconnect}; reconnectDelay={reconnectDelayDescription}; auth={authMode}...");
try
{
    await connection.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[conn] START FAILED: {ex.Message}");
    return;
}
connectedAt = DateTimeOffset.UtcNow;
Console.WriteLine($"[conn] connected: {connection.ConnectionId}");
PrintCommands();

while (true)
{
    var line = Console.ReadLine();
    if (line is null)
    {
        Console.WriteLine("[client] standard input closed; disposing connection.");
        break;
    }

    if (line.Length == 0)
    {
        Console.WriteLine("[client] empty line entered; disposing connection.");
        break;
    }

    if (line.Equals("/refresh", StringComparison.OrdinalIgnoreCase))
    {
        await RefreshAsync();
        continue;
    }

    if (line.StartsWith("/refresh-as ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            Console.WriteLine("Usage: /refresh-as <userId> <role>");
            continue;
        }

        await WithTemporaryAuthAsync(parts[1], parts[2], refreshLifetimeSeconds, refreshIncludesExpiration, false, () => RefreshAsync());
        continue;
    }

    if (line.StartsWith("/refresh-lifetime ", StringComparison.OrdinalIgnoreCase))
    {
        var value = line["/refresh-lifetime ".Length..].Trim();
        if (!int.TryParse(value, out var seconds) || seconds < 1)
        {
            Console.WriteLine("Usage: /refresh-lifetime <positive-seconds>");
            continue;
        }

        await WithTemporaryAuthAsync(userId, role, seconds, true, false, () => RefreshAsync());
        continue;
    }

    if (line.Equals("/refresh-no-exp", StringComparison.OrdinalIgnoreCase))
    {
        await WithTemporaryAuthAsync(userId, role, refreshLifetimeSeconds, false, false, () => RefreshAsync());
        continue;
    }

    if (line.Equals("/refresh-invalid", StringComparison.OrdinalIgnoreCase))
    {
        await WithTemporaryAuthAsync(userId, role, refreshLifetimeSeconds, true, true, () => RefreshAsync());
        continue;
    }

    if (line.StartsWith("/parallel-lifetimes ", StringComparison.OrdinalIgnoreCase))
    {
        var values = line["/parallel-lifetimes ".Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length < 2 || values.Length > 20 || values.Any(value => !int.TryParse(value, out var seconds) || seconds < 1))
        {
            Console.WriteLine("Usage: /parallel-lifetimes <positive-seconds,...> (2-20 values)");
            continue;
        }

        var lifetimes = values.Select(int.Parse).ToArray();
        for (var index = 0; index < lifetimes.Length; index++)
        {
            queuedRefreshTokens.Enqueue(new RefreshTokenRequest(
                $"parallel-lifetime-{index + 1}", userId, role, lifetimes[index], true));
        }
        Console.WriteLine($"[refresh] launching mixed lifetimes: {string.Join(',', lifetimes)}");
        await Task.WhenAll(Enumerable.Range(1, lifetimes.Length).Select(index => RefreshAsync($"parallel-lifetime-{index}")));
        continue;
    }

    if (line.StartsWith("/parallel", StringComparison.OrdinalIgnoreCase))
    {
        var value = line["/parallel".Length..].Trim();
        var count = int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 2, 20) : 2;
        Console.WriteLine($"[refresh] launching {count} concurrent refreshes...");
        await Task.WhenAll(Enumerable.Range(1, count).Select(index => RefreshAsync($"parallel-{index}")));
        continue;
    }

    if (line.StartsWith("/wait ", StringComparison.OrdinalIgnoreCase))
    {
        var value = line["/wait ".Length..].Trim();
        if (!int.TryParse(value, out var milliseconds) || milliseconds < 0 || milliseconds > 30000)
        {
            Console.WriteLine("Usage: /wait <milliseconds: 0-30000>");
            continue;
        }

        Console.WriteLine($"[client] waiting {milliseconds}ms...");
        await Task.Delay(milliseconds);
        continue;
    }

    if (line.Equals("/marker", StringComparison.OrdinalIgnoreCase))
    {
        // Invokes a hub method that returns Context.User's 'marker' claim, so we can confirm the
        // refreshed claim set was actually applied to the live server-side connection principal.
        var marker = await connection.InvokeAsync<string>("WhoAmIMarker");
        Console.WriteLine($"[marker] server-side Context.User marker={marker}");
        continue;
    }

    if (line.Equals("/status", StringComparison.OrdinalIgnoreCase))
    {
        var state = await connection.InvokeAsync<JsonElement>("WhoAmIState");
        var config = await http.GetStringAsync($"{serverBaseUrl}/sample-config");
        Console.WriteLine($"[status] connectionId={connection.ConnectionId}");
        Console.WriteLine($"[status] principal={state}");
        Console.WriteLine($"[status] host={config}");
        responseInspector.PrintLastResponse();
        continue;
    }

    if (line.StartsWith("/probe ", StringComparison.OrdinalIgnoreCase))
    {
        await ProbeRefreshEndpointAsync(line["/probe ".Length..].Trim());
        continue;
    }

    if (line.Equals("/claims", StringComparison.OrdinalIgnoreCase))
    {
        await PrintConnectionClaimsAsync();
        continue;
    }

    if (line.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        PrintCommands();
        continue;
    }

    // Fire-and-forget send. Serverless upstream forwards this as a "messages" event to the
    // [SignalRTrigger] (which then broadcasts). InvokeAsync would block waiting for a completion the
    // upstream trigger does not return; SendAsync is correct for both serverless and Default mode.
    await connection.SendAsync("SendToAll", line);
}

await connection.DisposeAsync();

async Task RefreshAsync(string label = "manual")
{
    try
    {
        Console.WriteLine($"[refresh:{label}] RefreshAuthenticationAsync()...");
        await connection.RefreshAuthenticationAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[refresh:{label}] rejected: {ex.Message}");
    }
}

async Task WithTemporaryAuthAsync(
    string temporaryUserId,
    string temporaryRole,
    int? temporaryLifetimeSeconds,
    bool temporaryIncludeExpiration,
    bool temporaryInvalidToken,
    Func<Task> action)
{
    var previous = (refreshUserId, refreshRole, refreshLifetimeSeconds, refreshIncludesExpiration, useInvalidToken);
    refreshUserId = temporaryUserId;
    refreshRole = temporaryRole;
    refreshLifetimeSeconds = temporaryLifetimeSeconds;
    refreshIncludesExpiration = temporaryIncludeExpiration;
    useInvalidToken = temporaryInvalidToken;
    try
    {
        await action();
    }
    finally
    {
        (refreshUserId, refreshRole, refreshLifetimeSeconds, refreshIncludesExpiration, useInvalidToken) = previous;
    }
}

static bool IsOff(string value) =>
    value.Equals("off", StringComparison.OrdinalIgnoreCase)
    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
    || value.Equals("no", StringComparison.OrdinalIgnoreCase);

static Dictionary<string, string>? ParseNamedArguments(string[] arguments)
{
    if (!arguments.Any(argument => argument.StartsWith("--", StringComparison.Ordinal)))
    {
        return null;
    }

    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "server-url", "user-id", "role", "transport", "auto-refresh", "auth-mode",
        "lifetime-seconds", "expiration", "scenario", "auto-reconnect", "negotiate-version",
        "ignore-refreshed-access-token", "reconnect-delay-seconds", "refresh-before-seconds",
        "endpoint-name",
    };
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Positional argument '{argument}' cannot be mixed with named arguments.");
        }

        var option = argument[2..];
        if (option.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            result["help"] = string.Empty;
            continue;
        }

        var separator = option.IndexOf('=');
        var name = separator >= 0 ? option[..separator] : option;
        if (!supported.Contains(name))
        {
            throw new ArgumentException($"Unknown argument '--{name}'.");
        }
        if (result.ContainsKey(name))
        {
            throw new ArgumentException($"Argument '--{name}' was specified more than once.");
        }

        string value;
        if (separator >= 0)
        {
            value = option[(separator + 1)..];
        }
        else if (++index < arguments.Length && !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            value = arguments[index];
        }
        else
        {
            throw new ArgumentException($"Argument '--{name}' requires a value.");
        }

        result[name] = value;
    }
    return result;
}

static void PrintUsage() => Console.WriteLine(
    "Usage: dotnet run -- [options]\n" +
    "  --server-url <url>           App server base URL (default: http://localhost:5121)\n" +
    "  --user-id <id>               User ID (default: alice)\n" +
    "  --role <role>                User role (default: user)\n" +
    "  --transport <ws|sse|lp|all>  Transport (default: ws)\n" +
    "  --auto-refresh <on|off>       Automatic auth refresh (default: on)\n" +
    "  --auth-mode <token|cookie|none> Authentication mode (default: token)\n" +
    "  --lifetime-seconds <seconds> App token lifetime override\n" +
    "  --expiration <on|off>         Include app-token expiration (default: on)\n" +
    "  --scenario <name>             Serverless scenario (default: standard)\n" +
    "  --auto-reconnect <on|off>     Automatic reconnect (default: on)\n" +
    "  --reconnect-delay-seconds <n> Delay before each reconnect attempt (default: client policy)\n" +
    "  --refresh-before-seconds <n>  Refresh lead time (default: 20)\n" +
    "  --endpoint-name <name>         Select a named Serverless endpoint for negotiate\n" +
    "  --negotiate-version <0|1>     Negotiate protocol version\n" +
    "  --ignore-refreshed-access-token <on|off> Simulate a client that ignores refresh accessToken\n" +
    "  --help                        Show this help");

static void PrintCommands() => Console.WriteLine(
    "Commands: message=broadcast; /refresh; /refresh-as <user> <role>; " +
    "/refresh-lifetime <seconds>; /refresh-no-exp; /refresh-invalid; /parallel [count]; " +
    "/parallel-lifetimes <seconds,...>; " +
    "/wait <milliseconds>; /probe <get|missing-header|missing-id|query-only|empty-header|header-and-query|no-auth|invalid-auth|unknown>; " +
    "/claims; /marker; /status; /help; empty line=exit.");

async Task PrintConnectionClaimsAsync()
{
    using var configDocument = JsonDocument.Parse(await http.GetStringAsync($"{serverBaseUrl}/sample-config"));
    var isServerless = configDocument.RootElement.TryGetProperty("mode", out var mode)
        && mode.ValueEquals("Serverless");
    if (!isServerless)
    {
        var claims = await connection.InvokeAsync<JsonElement>("WhoAmIClaims", Volatile.Read(ref completedAuthenticationRefreshes));
        Console.WriteLine($"[claims] Default hub principal; body={claims}");
        return;
    }

    var connectionToken = responseInspector.ConnectionToken;
    if (string.IsNullOrEmpty(connectionToken))
    {
        Console.WriteLine("[claims] no negotiated connection token was captured");
        return;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"{serverBaseUrl}/hub/claims?id={Uri.EscapeDataString(connectionToken)}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAppTokenAsync());
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"[claims] {(int)response.StatusCode} {response.StatusCode}; body={body}");
}

async Task ProbeRefreshEndpointAsync(string probe)
{
    probe = probe.ToLowerInvariant();
    var connectionToken = responseInspector.ConnectionToken;
    if (string.IsNullOrEmpty(connectionToken))
    {
        Console.WriteLine("[probe] no negotiated connection token was captured");
        return;
    }

    var method = probe.Equals("get", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post;
    var headerToken = probe switch
    {
        "missing-header" or "missing-id" or "query-only" => null,
        "empty-header" => string.Empty,
        "unknown" => "unknown-connection-token",
        _ => connectionToken,
    };
    // Only deliberate compatibility probes put a token in the query. Refresh never falls back to it.
    var queryToken = probe switch
    {
        "query-only" or "empty-header" => connectionToken,
        "header-and-query" => "unknown-connection-token",
        _ => null,
    };
    var uriBuilder = new UriBuilder(hubUrl)
    {
        Path = $"{new Uri(hubUrl).AbsolutePath.TrimEnd('/')}/refresh",
    };
    var query = uriBuilder.Query.TrimStart('?');
    uriBuilder.Query = queryToken is null
        ? query
        : string.IsNullOrEmpty(query)
            ? $"id={Uri.EscapeDataString(queryToken)}"
            : $"{query}&id={Uri.EscapeDataString(queryToken)}";
    var uri = uriBuilder.Uri;
    using var request = new HttpRequestMessage(method, uri);
    if (headerToken is not null)
    {
        request.Headers.TryAddWithoutValidation("X-SignalR-Connection-Token", headerToken);
    }

    if (!probe.Equals("no-auth", StringComparison.OrdinalIgnoreCase))
    {
        var token = probe.Equals("invalid-auth", StringComparison.OrdinalIgnoreCase)
            ? "invalid-app-token"
            : await GetAppTokenAsync();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"[probe:{probe}] {(int)response.StatusCode} {response.StatusCode}; body={body}");
}

// Shape of the sample server's POST /token response.
internal sealed record TokenResponse(
    string AccessToken,
    long? ExpiresAt,
    int? ExpiresIn,
    int? TokenLifetimeSeconds);

internal sealed record RefreshTokenRequest(
    string Label,
    string UserId,
    string Role,
    int? LifetimeSeconds,
    bool IncludeExpiration);

internal sealed class ResponseInspector
{
    private readonly object _sync = new();
    private string? _lastResponse;
    private string? _serviceTokenClaims;

    public string? ConnectionToken { get; private set; }

    public void Record(string response, string? connectionToken, string? serviceTokenClaims)
    {
        lock (_sync)
        {
            _lastResponse = response;
            ConnectionToken ??= connectionToken;
            _serviceTokenClaims = serviceTokenClaims ?? _serviceTokenClaims;
        }
    }

    public void PrintLastResponse()
    {
        lock (_sync)
        {
            Console.WriteLine($"[status] last negotiate/refresh={_lastResponse ?? "(none)"}");
            Console.WriteLine($"[status] service access token claims={_serviceTokenClaims ?? "(none)"}");
        }
    }
}

internal sealed class SignalRResponseInspectorHandler(ResponseInspector inspector, bool ignoreRefreshedAccessToken) : DelegatingHandler
{
    private static readonly TimeSpan UtcPlus8 = TimeSpan.FromHours(8);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri?.AbsolutePath.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) != true
            && request.RequestUri?.AbsolutePath.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase) != true)
        {
            return response;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        var (summary, connectionToken, serviceTokenClaims) = Summarize(
            request.RequestUri.AbsolutePath,
            response.StatusCode,
            body);
        inspector.Record(summary, connectionToken, serviceTokenClaims);
        Console.WriteLine($"[http] {summary}");

        if (ignoreRefreshedAccessToken
            && response.IsSuccessStatusCode
            && request.RequestUri.AbsolutePath.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase))
        {
            response.Content = RemoveAccessToken(response.Content, body);
            Console.WriteLine("[compat] ignored accessToken from refresh response");
        }
        return response;
    }

    private static HttpContent RemoveAccessToken(HttpContent? originalContent, string body)
    {
        using var document = JsonDocument.Parse(body);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("accessToken"))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(stream.ToArray());
        if (originalContent?.Headers.ContentType is { } contentType)
        {
            content.Headers.ContentType = contentType;
        }
        return content;
    }

    private static (string Summary, string? ConnectionToken, string? ServiceTokenClaims) Summarize(
        string path,
        HttpStatusCode statusCode,
        string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var advertisedLifetime = root.TryGetProperty("tokenLifetimeSeconds", out var lifetime)
                ? lifetime.ToString()
                : "omitted";
            var (jwtLifetime, serviceTokenClaims) = root.TryGetProperty("accessToken", out var token)
                ? ReadJwt(token.GetString())
                : ("n/a", null);
            var error = root.TryGetProperty("error", out var errorProperty)
                ? $"; error={errorProperty.GetString()}"
                : string.Empty;
            var redirect = root.TryGetProperty("url", out var urlProperty)
                ? $"; url={urlProperty.GetString()}"
                : string.Empty;
            var connectionToken = root.TryGetProperty("connectionToken", out var connectionTokenProperty)
                ? connectionTokenProperty.GetString()
                : root.TryGetProperty("connectionId", out var connectionIdProperty)
                    ? connectionIdProperty.GetString()
                    : null;
            return (
                $"{path} => {(int)statusCode}; tokenLifetimeSeconds={advertisedLifetime}; jwtLifetime={jwtLifetime}{error}{redirect}",
                connectionToken,
                serviceTokenClaims);
        }
        catch (JsonException)
        {
            return ($"{path} => {(int)statusCode}; body={body}", null, null);
        }
    }

    private static (string Lifetime, string? Claims) ReadJwt(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return ("n/a", null);
        }

        try
        {
            var segments = token.Split('.');
            if (segments.Length < 2)
            {
                return ("not-jwt", null);
            }

            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            var claims = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            if (!root.TryGetProperty("exp", out var exp) || !root.TryGetProperty("iat", out var iat))
            {
                return ("missing-exp-or-iat", claims);
            }

            var issuedAtUnixSeconds = iat.GetInt64();
            var expiresAtUnixSeconds = exp.GetInt64();
            var issuedAtUtcPlus8 = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds).ToOffset(UtcPlus8);
            var expiresAtUtcPlus8 = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds).ToOffset(UtcPlus8);
            claims = FormatClaims(root, issuedAtUtcPlus8, expiresAtUtcPlus8);
            return (
                $"{expiresAtUnixSeconds - issuedAtUnixSeconds}s; " +
                $"iat={issuedAtUtcPlus8:yyyy-MM-dd HH:mm:ss zzz}; " +
                $"exp={expiresAtUtcPlus8:yyyy-MM-dd HH:mm:ss zzz}; " +
                $"aud={root.GetProperty("aud")}",
                claims);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException)
        {
            return ("unreadable", null);
        }
    }

    private static string FormatClaims(
        JsonElement root,
        DateTimeOffset issuedAtUtcPlus8,
        DateTimeOffset expiresAtUtcPlus8)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteString("iatUtcPlus8", issuedAtUtcPlus8.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            writer.WriteString("expUtcPlus8", expiresAtUtcPlus8.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
