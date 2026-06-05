using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;

[Authorize]
public sealed class ChatHub : Hub
{
    public Task SendToAll(string message) =>
        Clients.All.SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    public Task SendToCurrentUser(string message) =>
        Clients.User(Context.UserIdentifier!).SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    public Task SendToUser(string userId, string message) =>
        Clients.User(userId).SendAsync("ReceiveMessage", Context.UserIdentifier, message);

    /// <summary>
    /// Returns the token <c>exp</c> claim (unix seconds) of the caller, so the UI can show
    /// the remaining auth lifetime as seen on the server.
    /// </summary>
    public long WhoAmIExp()
    {
        var exp = Context.User?.FindFirst(JwtRegisteredClaimNames.Exp)?.Value
                  ?? Context.User?.FindFirst("exp")?.Value;
        return long.TryParse(exp, out var seconds) ? seconds : 0;
    }

    public override Task OnConnectedAsync() =>
        Clients.Caller.SendAsync(
            "ReceiveMessage",
            "server",
            $"Connected through Azure SignalR Service as {Context.UserIdentifier}.");
}
