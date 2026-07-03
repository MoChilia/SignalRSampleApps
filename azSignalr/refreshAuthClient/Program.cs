using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Json;

// Minimal .NET client for the refreshAuthApp server. Exercises the locally-built aspnetcore
// SignalR client's Default-mode auth refresh (WithAuthenticationRefresh + {hubUrl}/refresh).
//
// Usage: dotnet run [serverBaseUrl] [userId] [role] [transport] [autorefresh]
//   transport   = ws | sse | lp | all   (default ws)
//   autorefresh = on | off               (default on)
//   e.g. dotnet run http://localhost:5121 alice user sse off

var serverBaseUrl = (args.Length > 0 ? args[0] : "http://localhost:5121").TrimEnd('/');
var userId = args.Length > 1 ? args[1] : "alice";
var role = args.Length > 2 ? args[2] : "user";
var transportArg = (args.Length > 3 ? args[3] : "ws").ToLowerInvariant();
var autoRefresh = !(args.Length > 4 && (
    string.Equals(args[4], "off", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[4], "false", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[4], "no", StringComparison.OrdinalIgnoreCase)));
var hubUrl = $"{serverBaseUrl}/hub";

var transports = transportArg switch
{
    "sse" => HttpTransportType.ServerSentEvents,
    "lp" or "longpolling" => HttpTransportType.LongPolling,
    "ws" or "websockets" => HttpTransportType.WebSockets,
    "all" => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
    _ => HttpTransportType.WebSockets,
};

using var http = new HttpClient();

// App-plane token factory. The Azure SignalR Default-mode refresh posts to {hubUrl}/refresh using
// THIS provider (a fresh app token minted from the sample's /token endpoint), while transport
// requests use the service token captured at the negotiate redirect. Minting a fresh token on each
// call is what lets /refresh present a still-valid app token to advance the connection's lifetime.
async Task<string?> GetAppTokenAsync()
{
    var resp = await http.PostAsJsonAsync($"{serverBaseUrl}/token", new { userId, role });
    resp.EnsureSuccessStatusCode();
    var payload = await resp.Content.ReadFromJsonAsync<TokenResponse>();
    Console.WriteLine($"[token] minted app token (expiresIn={payload?.ExpiresIn}s)");
    return payload?.AccessToken;
}

var connection = new HubConnectionBuilder()
    .WithUrl(hubUrl, options =>
    {
        options.AccessTokenProvider = GetAppTokenAsync;
        options.Transports = transports;
    })
    .WithAuthenticationRefresh(o =>
    {
        // Toggle via the 5th arg. When off, no refresh timer is scheduled: the token expires and the
        // server's CloseOnAuthenticationExpiration aborts the connection, then WithAutomaticReconnect
        // renegotiates. Manual RefreshAuthenticationAsync ('/refresh') still works regardless.
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
    })
    .WithAutomaticReconnect()
    .Build();

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
    Console.WriteLine($"[conn] closed: {error?.Message ?? "(no error)"}");
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

Console.WriteLine($"Connecting to {hubUrl} as '{userId}' ({role}) over {transports}; autoRefresh={autoRefresh}...");
await connection.StartAsync();
Console.WriteLine($"[conn] connected: {connection.ConnectionId}");
Console.WriteLine("Commands: message=broadcast (SendToAll), '/refresh'=force refresh, '/marker'=query server-side marker claim, empty line / Ctrl+C to exit.");

while (true)
{
    var line = Console.ReadLine();
    if (string.IsNullOrEmpty(line))
    {
        break;
    }

    if (line.Equals("/refresh", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[refresh] manual RefreshAuthenticationAsync()...");
        await connection.RefreshAuthenticationAsync();
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

    await connection.InvokeAsync("SendToAll", line);
}

await connection.DisposeAsync();

// Shape of the sample server's POST /token response.
internal sealed record TokenResponse(
    string AccessToken,
    long ExpiresAt,
    int ExpiresIn,
    int TokenLifetimeSeconds);
