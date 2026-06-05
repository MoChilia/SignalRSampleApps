using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Periodically pushes a "ping" message from the server to all connected clients.
/// This lets you observe whether the SSE stream (server → client) stays alive
/// after the access token expires (the client POST will fail, but the SSE GET may persist).
/// </summary>
public sealed class ServerPingService : BackgroundService
{
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<ServerPingService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    public ServerPingService(IHubContext<ChatHub> hubContext, ILogger<ServerPingService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seq = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);
            seq++;
            var message = $"server-ping #{seq} at {DateTimeOffset.UtcNow:HH:mm:ss}";
            _logger.LogInformation("[ServerPing] Broadcasting: {Message}", message);
            await _hubContext.Clients.All.SendAsync("ReceiveMessage", "server-ping", message, stoppingToken);
        }
    }
}
