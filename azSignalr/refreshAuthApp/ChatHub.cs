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

    /// <summary>
    /// Returns the caller's current <c>marker</c> claim as seen by the live server-side principal.
    /// Call before and after a refresh to confirm the refreshed claim set was actually applied to
    /// the running connection (the value should change on each refresh).
    /// </summary>
    public string WhoAmIMarker() => Context.User?.FindFirst("marker")?.Value ?? "(none)";

    public override Task OnConnectedAsync() =>
        Clients.Caller.SendAsync(
            "ReceiveMessage",
            "server",
            $"Connected through Azure SignalR Service as {Context.UserIdentifier}.");

    /// <summary>
    /// Invoked after ASRS applies a refreshed principal to this connection (Default-mode
    /// {hubUrl}/refresh). Runs only when the claims changed; same-user is enforced by the
    /// runtime, so <see cref="HubCallerContext.UserIdentifier"/> is unchanged here.
    /// </summary>
    public override Task OnAuthenticationRefreshedAsync()
    {
        var marker = Context.User?.FindFirst("marker")?.Value ?? "(none)";
        return Clients.Caller.SendAsync(
            "ReceiveMessage",
            "server",
            $"Authentication refreshed on the live connection; still {Context.UserIdentifier} (marker={marker}).");
    }
}
