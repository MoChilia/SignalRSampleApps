using Microsoft.AspNetCore.Authentication.JwtBearer;
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
            });

        builder.Services.AddAuthorization();
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
            options.AccessTokenLifetime = AppTokenProvider.DefaultLifetime;
            options.ClaimsProvider = context =>
            {
                var claims = new List<Claim>();
                if (context.User.FindFirst(ClaimTypes.NameIdentifier) is { } userIdClaim)
                {
                    claims.Add(userIdClaim);
                }
                else if (context.User.FindFirst(JwtRegisteredClaimNames.Sub) is { } subjectClaim)
                {
                    claims.Add(subjectClaim);
                }

                // Flow the per-mint marker through negotiate and refresh so the application claim set
                // changes on every refresh (drives OnAuthenticationRefreshedAsync each time).
                if (context.User.FindFirst("marker") is { } markerClaim)
                {
                    claims.Add(markerClaim);
                }

                return claims;
            };
        });

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Redirect("/index.html"));
        app.MapGet("/favicon.ico", () => Results.NoContent());

        // Initial token issuance. Mirrors the negotiate response shape from the refresh-auth spec.
        // we advertise `tokenLifetimeSeconds` so the client knows when to call {hubUrl}/refresh.
        app.MapPost("/token", (DemoTokenRequest request, AppTokenProvider tokens) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserId)
                || string.IsNullOrWhiteSpace(request.Role))
            {
                return Results.BadRequest("userId and role are required.");
            }

            var token = tokens.CreateToken(request.UserId.Trim(), request.Role.Trim());
            var tokenLifetimeSeconds = (int)(token.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            return Results.Ok(new
            {
                accessToken = token.AccessToken,
                tokenType = "Bearer",
                expiresAt = token.ExpiresAt.ToUnixTimeSeconds(),
                expiresIn = tokenLifetimeSeconds,
                tokenLifetimeSeconds,
            });
        });

        app.MapHub<ChatHub>(HubPath, options =>
        {
            options.CloseOnAuthenticationExpiration = true;

            // Opt into the shipped Default-mode refresh feature.
            options.EnableAuthenticationRefresh = true;

            // Accept/reject gate. Runs on this app server (with the real HttpContext) before ASRS mutates
            // anything. Demo policy: reject the refresh when the new token's role is "blocked" so the
            // 403 permission_change_rejected path can be exercised (run the client with role "blocked").
            // Returning false changes nothing on the live connection.
            options.OnAuthenticationRefresh = context =>
            {
                var isAuthenticated = context.NewUser?.Identity?.IsAuthenticated == true;
                var isBlocked = string.Equals(
                    context.NewUser?.FindFirst("role")?.Value, "blocked", StringComparison.OrdinalIgnoreCase);
                var accept = isAuthenticated && !isBlocked;

                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OnAuthenticationRefresh")
                    .LogInformation(
                        "OnAuthenticationRefresh gate: user={User} accept={Accept} (authenticated={Authenticated}, blocked={Blocked})",
                        context.NewUser?.Identity?.Name, accept, isAuthenticated, isBlocked);

                return ValueTask.FromResult(accept);
            };
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The route under which <see cref="ChatHub"/> is mapped. Shared with the integration test.
    /// </summary>
    public const string HubPath = "/hub";
}

public sealed record DemoTokenRequest(string UserId, string Role);

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
