using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Json;

// Minimal .NET client for the refreshAuthApp server. Exercises the locally-built aspnetcore
// SignalR client's Default-mode auth refresh (WithAuthenticationRefresh + {hubUrl}/refresh).
//
// Usage: dotnet run [serverBaseUrl] [userId] [role]
//   e.g. dotnet run http://localhost:5121 alice user

var serverBaseUrl = (args.Length > 0 ? args[0] : "http://localhost:5121").TrimEnd('/');
var userId = args.Length > 1 ? args[1] : "alice";
var role = args.Length > 2 ? args[2] : "user";
var hubUrl = $"{serverBaseUrl}/hub";

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
    })
    .WithAuthenticationRefresh(o =>
    {
        o.EnableAutoRefresh = true;
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

connection.Closed += error =>
{
    Console.WriteLine($"[conn] closed: {error?.Message ?? "(no error)"}");
    return Task.CompletedTask;
};

Console.WriteLine($"Connecting to {hubUrl} as '{userId}' ({role})...");
await connection.StartAsync();
Console.WriteLine($"[conn] connected: {connection.ConnectionId}");
Console.WriteLine("Commands: type a message to broadcast (SendToAll), '/refresh' to force a refresh, empty line / Ctrl+C to exit.");

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

    await connection.InvokeAsync("SendToAll", line);
}

await connection.DisposeAsync();

// Shape of the sample server's POST /token response.
internal sealed record TokenResponse(
    string AccessToken,
    long ExpiresAt,
    int ExpiresIn,
    int TokenLifetimeSeconds);
