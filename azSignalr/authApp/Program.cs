using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

const string connectionStringKey = "Azure:SignalR:ConnectionString";
var connectionString = builder.Configuration[connectionStringKey];

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        $"Missing Azure SignalR connection string. Set '{connectionStringKey}' with user secrets or the 'Azure__SignalR__ConnectionString' environment variable.");
}

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5120");
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
builder.Services.AddHostedService<ServerPingService>();
builder.Services.AddSignalR().AddAzureSignalR(options =>
{
    options.ConnectionString = connectionString;
    // Align the service access token lifetime with the app JWT so the SSE stream
    // is closed promptly when the app token would have expired.
    // This makes the token-expiry demo observable with short-lived tokens.
    options.AccessTokenLifetime = TimeSpan.FromSeconds(30);
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
app.MapPost("/token", (DemoTokenRequest request, AppTokenProvider tokens) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId)
        || string.IsNullOrWhiteSpace(request.Role))
    {
        return Results.BadRequest("userId and role are required.");
    }

    var lifetime = request.LifetimeSeconds is > 0
        ? TimeSpan.FromSeconds(request.LifetimeSeconds.Value)
        : TimeSpan.FromHours(1);

    var (accessToken, expiresAt) = tokens.CreateToken(request.UserId.Trim(), request.Role.Trim(), lifetime);
    return Results.Ok(new
    {
        accessToken,
        tokenType = "Bearer",
        expiresAt = expiresAt.ToUnixTimeSeconds(),
        expiresIn = (int)(expiresAt - DateTimeOffset.UtcNow).TotalSeconds
    });
});

app.MapGet("/sse-expiry-demo", () => Results.Redirect("/sse-expiry-demo.html"));
app.MapHub<ChatHub>("/hub").RequireAuthorization();

app.Run();

public sealed record DemoTokenRequest(string UserId, string Role, int? LifetimeSeconds);