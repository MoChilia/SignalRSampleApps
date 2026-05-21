using Microsoft.AspNetCore.SignalR;

public sealed class ChatHub : Hub
{
    public Task NewMessage(long user, string message) =>
        Clients.All.SendAsync("messageReceived", user, message);
}