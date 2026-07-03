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
        builder.Services.AddSignalR().AddAzureSignalR(options =>
        {
            // https://github.com/Azure/azure-signalr/blob/dev/src/Microsoft.Azure.SignalR/ServiceOptions.cs
            options.ConnectionString = connectionString;
            options.AccessTokenLifetime = AppTokenProvider.DefaultLifetime;
            options.ClaimsProvider = context =>
                context.User.FindFirst(ClaimTypes.NameIdentifier) is { } userId
                    ? new[] { userId }
                    : context.User.FindFirst(JwtRegisteredClaimNames.Sub) is { } subject
                        ? new[] { subject }
                        : [];
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

            // Optional accept/reject gate.
            options.OnAuthenticationRefresh = context =>
                ValueTask.FromResult(context.NewUser?.Identity?.IsAuthenticated == true);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The route under which <see cref="ChatHub"/> is mapped. Shared with the integration test.
    /// </summary>
    public const string HubPath = "/hub";
}

public sealed record DemoTokenRequest(string UserId, string Role);
