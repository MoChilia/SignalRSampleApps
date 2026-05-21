using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

[Authorize]
public sealed class ChatHub : Hub
{
    public Task SendToAll(string message) =>
        Clients.All.SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    public Task SendToCurrentUser(string message) =>
        Clients.User(Context.UserIdentifier!).SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    public Task SendToUser(string userId, string message) =>
        Clients.User(userId).SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    public override Task OnConnectedAsync() =>
        Clients.Caller.SendAsync(
            "ReceiveMessage",
            "server",
            $"Connected as {Context.UserIdentifier}.");
}