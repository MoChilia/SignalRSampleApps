using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

Program.BuildApp(args).Run();

public partial class Program
{
    public const string ConnectionStringKey = "Azure:SignalR:ConnectionString";

    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var connectionString = builder.Configuration[ConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing Azure SignalR connection string. Set '{ConnectionStringKey}' with user secrets or the 'Azure__SignalR__ConnectionString' environment variable.");
        }

        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5121");
        builder.Logging.AddFilter("Microsoft.Azure.SignalR.DefaultServiceEventHandler", LogLevel.Warning);

        var appTokenLifetime = TimeSpan.FromSeconds(
            Math.Max(1, builder.Configuration.GetValue("AuthRefresh:AppTokenLifetimeSeconds", (int)AppTokenProvider.DefaultLifetime.TotalSeconds)));
        var serviceTokenLifetime = TimeSpan.FromSeconds(
            Math.Max(1, builder.Configuration.GetValue("AuthRefresh:ServiceTokenLifetimeSeconds", (int)AppTokenProvider.DefaultLifetime.TotalSeconds)));
        var enableAuthenticationRefresh = builder.Configuration.GetValue("AuthRefresh:Enabled", true);
        var closeOnAuthenticationExpiration = builder.Configuration.GetValue("AuthRefresh:CloseOnAuthenticationExpiration", true);
        var enableRefreshGate = builder.Configuration.GetValue("AuthRefresh:GateEnabled", true);
        var requireNullRefreshExpiration = builder.Configuration.GetValue("AuthRefresh:RequireNullExpiration", false);
        var throwInRefreshGate = builder.Configuration.GetValue("AuthRefresh:ThrowInGate", false);
        var serverStickyMode = builder.Configuration.GetValue("Azure:SignalR:ServerStickyMode", ServerStickyMode.Disabled);
        var maximumAuthenticationExpirationSeconds = builder.Configuration.GetValue<int?>("AuthRefresh:MaximumAuthenticationExpirationSeconds");
        if (maximumAuthenticationExpirationSeconds is <= 0)
        {
            throw new InvalidOperationException("AuthRefresh:MaximumAuthenticationExpirationSeconds must be positive when configured.");
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = AppTokenProvider.Issuer,
                    ValidateAudience = true,
                    ValidAudience = AppTokenProvider.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppTokenProvider.SigningKey)),
                    ValidateLifetime = true,
                    RequireExpirationTime = false,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role"
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];

                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments("/hub"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddCookie(options =>
            {
                // Cookie auth owns a session, so it sets Properties.ExpiresUtc itself (from ExpireTimeSpan /
                // SignInAsync) and needs no OnTokenValidated-style hook. SlidingExpiration re-issues the cookie
                // on {hubUrl}/refresh, advancing the connection's auth deadline (an expiration-only refresh).
                options.Cookie.Name = "AzureSignalRRefreshAuthSample";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = appTokenLifetime;
                options.SlidingExpiration = true;

                // Sliding expiration normally updates only the Set-Cookie response. The auth result for
                // this same request still contains the old ExpiresUtc, which would make auth refresh send
                // the old deadline to ASRS and mint a token with only the remaining lifetime. Advance the
                // current ticket properties as well so RefreshHandler observes the renewed expiration.
                options.Events.OnValidatePrincipal = context =>
                {
                    if (context.Request.Path.Equals($"{HubPath}/refresh", StringComparison.OrdinalIgnoreCase))
                    {
                        var now = DateTimeOffset.UtcNow;
                        context.Properties.IssuedUtc = now;
                        context.Properties.ExpiresUtc = now.Add(appTokenLifetime);
                        context.ShouldRenew = true;
                    }

                    return Task.CompletedTask;
                };

                // API / SignalR host: return status codes rather than redirecting to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // Accept EITHER scheme so /hub (negotiate + refresh) works in both token and cookie modes.
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                    JwtBearerDefaults.AuthenticationScheme,
                    CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddSingleton<AppTokenProvider>();
        builder.Services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
        // Optional server->client "Tick" broadcaster for the message-loss demo. With auth refresh the
        // stream is gapless; without it the connection is aborted at token expiry and the client reports
        // dropped ticks until WithAutomaticReconnect re-establishes. Disabled by default; toggle via config:
        //   dotnet run -- --Tick:Enabled=true         (or env: Tick__Enabled=true)
        //   dotnet run -- --Tick:IntervalSeconds=1    (broadcast interval, default 2)
        if (builder.Configuration.GetValue("Tick:Enabled", false))
        {
            builder.Services.AddHostedService<TickBroadcaster>();
        }
        builder.Services.AddSignalR().AddAzureSignalR(options =>
        {
            // https://github.com/Azure/azure-signalr/blob/dev/src/Microsoft.Azure.SignalR/ServiceOptions.cs
            options.ConnectionString = connectionString;
            options.AccessTokenLifetime = serviceTokenLifetime;
            options.ServerStickyMode = serverStickyMode;
            // Preserve the complete application principal so the sample can verify name/role claim-type
            // fidelity as well as the per-mint marker and exp updates after authentication refresh.
            options.ClaimsProvider = context => context.User.Claims;
        });

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Redirect("/index.html"));
        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapGet("/sample-config", () => Results.Ok(new
        {
            enableAuthenticationRefresh,
            closeOnAuthenticationExpiration,
            enableRefreshGate,
            maximumAuthenticationExpirationSeconds,
            appTokenLifetimeSeconds = (int)appTokenLifetime.TotalSeconds,
            serviceTokenLifetimeSeconds = (int)serviceTokenLifetime.TotalSeconds,
        }));

        // Initial token issuance. Mirrors the negotiate response shape from the refresh-auth spec.
        // we advertise `tokenLifetimeSeconds` so the client knows when to call {hubUrl}/refresh.
        app.MapPost("/token", (DemoTokenRequest request, AppTokenProvider tokens) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserId)
                || string.IsNullOrWhiteSpace(request.Role))
            {
                return Results.BadRequest("userId and role are required.");
            }

            var lifetime = request.LifetimeSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Max(1, seconds))
                : appTokenLifetime;
            var token = tokens.CreateToken(request.UserId.Trim(), request.Role.Trim(), lifetime, request.IncludeExpiration);
            var tokenLifetimeSeconds = token.ExpiresAt is { } expiration
                ? Math.Max(0, (int)(expiration - DateTimeOffset.UtcNow).TotalSeconds)
                : (int?)null;
            return Results.Ok(new
            {
                accessToken = token.AccessToken,
                tokenType = "Bearer",
                expiresAt = token.ExpiresAt?.ToUnixTimeSeconds(),
                expiresIn = tokenLifetimeSeconds,
                tokenLifetimeSeconds,
            });
        });

        // Cookie-auth login: issues the auth cookie (with an ExpiresUtc) the client reuses for
        // negotiate/refresh. Cookie mode is the alternative to the /token + bearer flow above.
        app.MapPost("/login", async (DemoTokenRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserId)
                || string.IsNullOrWhiteSpace(request.Role))
            {
                return Results.BadRequest("userId and role are required.");
            }

            var now = DateTimeOffset.UtcNow;
            var lifetime = request.LifetimeSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Max(1, seconds))
                : appTokenLifetime;
            var expiresAt = now.Add(lifetime);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, request.UserId.Trim()),
                new("name", request.UserId.Trim()),
                new("role", request.Role.Trim()),
                // Fixed for the cookie's lifetime. Unlike the per-mint /token marker, cookie refresh is
                // expiration-only (claims unchanged), so OnAuthenticationRefreshedAsync is not raised.
                new("marker", Guid.NewGuid().ToString("N")),
            };
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    AllowRefresh = true,
                    ExpiresUtc = expiresAt,
                });

            var tokenLifetimeSeconds = (int)lifetime.TotalSeconds;
            return Results.Ok(new
            {
                userId = request.UserId.Trim(),
                role = request.Role.Trim(),
                expiresAt = expiresAt.ToUnixTimeSeconds(),
                expiresIn = tokenLifetimeSeconds,
                tokenLifetimeSeconds,
            });
        });

        app.MapHub<ChatHub>(HubPath, options =>
        {
            options.CloseOnAuthenticationExpiration = closeOnAuthenticationExpiration;

            // Opt into the shipped Default-mode refresh feature.
            options.EnableAuthenticationRefresh = enableAuthenticationRefresh;
            if (maximumAuthenticationExpirationSeconds is { } maximumSeconds)
            {
                options.MaximumAuthenticationExpiration = TimeSpan.FromSeconds(maximumSeconds);
            }

            // Accept/reject gate. Runs on this app server (with the real HttpContext) before ASRS mutates
            // anything. Demo policy: reject the refresh when the new token's role is "blocked" so the
            // 403 permission_change_rejected path can be exercised (run the client with role "blocked").
            // Returning false changes nothing on the live connection.
            if (enableRefreshGate)
            {
                options.OnAuthenticationRefresh = context =>
                {
                    if (throwInRefreshGate)
                    {
                        throw new InvalidOperationException("Injected authentication refresh gate failure.");
                    }

                    var isAuthenticated = context.NewUser?.Identity?.IsAuthenticated == true;
                    var isBlocked = string.Equals(
                        context.NewUser?.FindFirst("role")?.Value, "blocked", StringComparison.OrdinalIgnoreCase);
                    var expirationAccepted = !requireNullRefreshExpiration || context.NewExpiration is null;
                    var accept = isAuthenticated && !isBlocked && expirationAccepted;

                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("OnAuthenticationRefresh")
                        .LogInformation(
                            "OnAuthenticationRefresh gate: user={User} accept={Accept} (authenticated={Authenticated}, blocked={Blocked}, newExpiration={NewExpiration})",
                            context.NewUser?.Identity?.Name, accept, isAuthenticated, isBlocked, context.NewExpiration);

                    return Task.FromResult(accept);
                };
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The route under which <see cref="ChatHub"/> is mapped. Shared with the integration test.
    /// </summary>
    public const string HubPath = "/hub";
}

public sealed record DemoTokenRequest(
    string UserId,
    string Role,
    int? LifetimeSeconds = null,
    bool IncludeExpiration = true);

// Broadcasts an incrementing "Tick" to all connected clients every 2 seconds via the Azure SignalR
// service connection. The monotonically increasing sequence number makes any connection drop visible:
// with auth refresh the client receives every tick; without it, ticks pause across the abort/reconnect.
internal sealed class TickBroadcaster(IHubContext<ChatHub> hub, IConfiguration config, ILogger<TickBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(1, config.GetValue("Tick:IntervalSeconds", 2));

        // Give the Azure SignalR server<->service connection time to establish before the first
        // broadcast; otherwise early ticks fail with FailedWritingMessageToServiceException
        // ("Unable to write message to endpoint") because the service connection isn't up yet.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var seq = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            seq++;
            try
            {
                await hub.Clients.All.SendAsync("Tick", seq, DateTimeOffset.UtcNow.ToString("HH:mm:ss"), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Transient while the service connection reconnects (e.g. right after startup). Keep ticking.
                logger.LogDebug("Tick #{Seq} not sent (service connection not ready): {Message}", seq, ex.Message);
            }
        }
    }
}
