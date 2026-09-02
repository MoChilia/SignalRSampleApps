using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;

[Authorize]
public sealed class ChatHub : Hub
{
    private static readonly object AuthenticationRefreshObserverKey = new();

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

    public async Task<object> WhoAmIClaims(int expectedRefreshCount = 0)
    {
        var observer = GetOrCreateAuthenticationRefreshObserver();
        if (observer is not null)
        {
            await observer.WaitForCountAsync(expectedRefreshCount, Context.ConnectionAborted);
        }
        return new
        {
            Claims = Context.User?.Claims.Select(claim => new { claim.Type, claim.Value }) ?? [],
            AuthenticationRefresh = observer?.GetSnapshot(),
        };
    }

    public object WhoAmIState() => new
    {
        Context.ConnectionId,
        Context.UserIdentifier,
        Name = Context.User?.Identity?.Name,
        IsAuthenticated = Context.User?.Identity?.IsAuthenticated == true,
        AuthenticationType = Context.User?.Identity?.AuthenticationType,
        NameClaimType = (Context.User?.Identity as System.Security.Claims.ClaimsIdentity)?.NameClaimType,
        RoleClaimType = (Context.User?.Identity as System.Security.Claims.ClaimsIdentity)?.RoleClaimType,
        IsInRole = Context.User?.IsInRole(Context.User?.FindFirst("role")?.Value ?? string.Empty) == true,
        Role = Context.User?.FindFirst("role")?.Value,
        Marker = Context.User?.FindFirst("marker")?.Value,
        ExpiresAt = WhoAmIExp(),
    };

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

    private AuthenticationRefreshObserver? GetOrCreateAuthenticationRefreshObserver()
    {
        lock (Context.Items)
        {
            if (Context.Items.TryGetValue(AuthenticationRefreshObserverKey, out var existing))
            {
                return (AuthenticationRefreshObserver)existing!;
            }

            var feature = Context.Features.Get<IConnectionAuthenticationRefreshFeature>();
            if (feature is null)
            {
                return null;
            }

            var observer = new AuthenticationRefreshObserver();
            observer.Attach(feature.OnAuthenticationRefreshed(
                static (refreshContext, state) => ((AuthenticationRefreshObserver)state!).Record(refreshContext.NewExpiration),
                observer));
            Context.Items[AuthenticationRefreshObserverKey] = observer;
            Context.ConnectionAborted.Register(static state => ((IDisposable)state!).Dispose(), observer);
            return observer;
        }
    }

    private sealed class AuthenticationRefreshObserver : IDisposable
    {
        private readonly object _lock = new();
        private IDisposable? _registration;
        private int _count;
        private DateTimeOffset? _previousExpiration;
        private DateTimeOffset? _newExpiration;

        public void Attach(IDisposable registration) => _registration = registration;

        public async Task WaitForCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 100 && GetCount() < expectedCount; attempt++)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        public object GetSnapshot()
        {
            lock (_lock)
            {
                return new
                {
                    Count = _count,
                    PreviousExpirationUnixSeconds = _previousExpiration?.ToUnixTimeSeconds(),
                    NewExpirationUnixSeconds = _newExpiration?.ToUnixTimeSeconds(),
                };
            }
        }

        public void Record(DateTimeOffset? newExpiration)
        {
            lock (_lock)
            {
                _count++;
                _previousExpiration = _newExpiration;
                _newExpiration = newExpiration;
            }
        }

        private int GetCount()
        {
            lock (_lock)
            {
                return _count;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _registration, null)?.Dispose();
    }
}
