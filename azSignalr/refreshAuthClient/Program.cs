using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// Minimal .NET client for the refreshAuthApp server. Exercises the locally-built aspnetcore
// SignalR client's Default-mode auth refresh (WithAuthenticationRefresh + {hubUrl}/refresh).
//
// Usage: dotnet run [serverBaseUrl] [userId] [role] [transport] [autorefresh] [authmode] [lifetimeSeconds] [expiration] [scenario] [autoreconnect]
//   transport    = ws | sse | lp | all   (default ws)
//   autorefresh  = on | off               (default on)
//   authmode     = token | cookie | none  (default token)
//   expiration   = on | off               (default on; token mode only)
//   autoreconnect = on | off              (default on)
//   e.g. dotnet run http://localhost:5121 alice user sse off cookie

var serverBaseUrl = (args.Length > 0 ? args[0] : "http://localhost:5121").TrimEnd('/');
var userId = args.Length > 1 ? args[1] : "alice";
var role = args.Length > 2 ? args[2] : "user";
var transportArg = (args.Length > 3 ? args[3] : "ws").ToLowerInvariant();
var autoRefresh = !(args.Length > 4 && (
    string.Equals(args[4], "off", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[4], "false", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[4], "no", StringComparison.OrdinalIgnoreCase)));
var authMode = (args.Length > 5 ? args[5] : "token").ToLowerInvariant();
var configuredLifetimeSeconds = args.Length > 6 && int.TryParse(args[6], out var lifetimeSeconds)
    ? lifetimeSeconds
    : (int?)null;
var includeExpiration = !(args.Length > 7 && IsOff(args[7]));
var scenario = args.Length > 8 ? args[8].ToLowerInvariant() : "standard";
var autoReconnect = !(args.Length > 9 && IsOff(args[9]));
var hubUrl = $"{serverBaseUrl}/hub";
var refreshUserId = userId;
var refreshRole = role;
var refreshLifetimeSeconds = configuredLifetimeSeconds;
var refreshIncludesExpiration = includeExpiration;
var useInvalidToken = false;
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

    var resp = await http.PostAsJsonAsync($"{serverBaseUrl}/token", new
    {
        userId = refreshUserId,
        role = refreshRole,
        lifetimeSeconds = refreshLifetimeSeconds,
        includeExpiration = refreshIncludesExpiration,
        scenario,
    });
    resp.EnsureSuccessStatusCode();
    var payload = await resp.Content.ReadFromJsonAsync<TokenResponse>();
    Console.WriteLine(
        $"[token] minted app token for {refreshUserId}/{refreshRole} " +
        $"(expiresIn={payload?.ExpiresIn?.ToString() ?? "none"}s)");
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
var connectionBuilder = new HubConnectionBuilder()
    .WithUrl(hubUrl, options =>
    {
        options.Transports = transports;
        options.HttpMessageHandlerFactory = innerHandler =>
            new SignalRResponseInspectorHandler(responseInspector) { InnerHandler = innerHandler };
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
        // Demo app/service tokens live ~60s; refresh ~20s before expiry so the flow is easy to watch.
        o.RefreshBeforeExpiration = TimeSpan.FromSeconds(20);
        o.OnAuthenticationRefreshed = ctx =>
        {
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
    connectionBuilder.WithAutomaticReconnect();
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

Console.WriteLine($"Connecting to {hubUrl} as '{userId}' ({role}) over {transports}; autoRefresh={autoRefresh}; autoReconnect={autoReconnect}; auth={authMode}...");
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

    if (line.StartsWith("/parallel", StringComparison.OrdinalIgnoreCase))
    {
        var value = line["/parallel".Length..].Trim();
        var count = int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 2, 20) : 2;
        Console.WriteLine($"[refresh] launching {count} concurrent refreshes...");
        await Task.WhenAll(Enumerable.Range(1, count).Select(index => RefreshAsync($"parallel-{index}")));
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

static void PrintCommands() => Console.WriteLine(
    "Commands: message=broadcast; /refresh; /refresh-as <user> <role>; " +
    "/refresh-lifetime <seconds>; /refresh-no-exp; /refresh-invalid; /parallel [count]; " +
    "/probe <get|missing-id|no-auth|invalid-auth|unknown>; /claims; /marker; /status; /help; empty line=exit.");

async Task PrintConnectionClaimsAsync()
{
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
    var connectionToken = responseInspector.ConnectionToken;
    if (string.IsNullOrEmpty(connectionToken))
    {
        Console.WriteLine("[probe] no negotiated connection token was captured");
        return;
    }

    var method = probe.Equals("get", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post;
    var id = probe switch
    {
        "missing-id" => null,
        "unknown" => "unknown-connection-token",
        _ => connectionToken,
    };
    var uri = $"{hubUrl}/refresh" + (id is null ? string.Empty : $"?id={Uri.EscapeDataString(id)}");
    using var request = new HttpRequestMessage(method, uri);

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

internal sealed class SignalRResponseInspectorHandler(ResponseInspector inspector) : DelegatingHandler
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
        return response;
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
            var connectionToken = root.TryGetProperty("connectionToken", out var connectionTokenProperty)
                ? connectionTokenProperty.GetString()
                : null;
            return (
                $"{path} => {(int)statusCode}; tokenLifetimeSeconds={advertisedLifetime}; jwtLifetime={jwtLifetime}{error}",
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
