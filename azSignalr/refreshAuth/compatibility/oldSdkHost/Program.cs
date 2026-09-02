using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["Azure:SignalR:ConnectionString"]
    ?? throw new InvalidOperationException("Set Azure:SignalR:ConnectionString.");

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5222");
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
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.Request.Path.StartsWithSegments("/hub"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<AppTokenProvider>();
builder.Services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
builder.Services.AddSignalR().AddAzureSignalR(options =>
{
    options.ConnectionString = connectionString;
    options.ClaimsProvider = context => context.User.Claims;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/token", (TokenRequest request, AppTokenProvider tokens) =>
{
    var lifetime = TimeSpan.FromSeconds(Math.Max(1, request.LifetimeSeconds ?? 10));
    var token = tokens.CreateToken(request.UserId ?? "alice", request.Role ?? "user", lifetime, request.IncludeExpiration);
    return Results.Ok(new
    {
        accessToken = token.AccessToken,
        expiresAt = token.ExpiresAt?.ToUnixTimeSeconds(),
        expiresIn = token.ExpiresAt is { } expiration
            ? Math.Max(0, (int)(expiration - DateTimeOffset.UtcNow).TotalSeconds)
            : (int?)null,
    });
});
app.MapGet("/sample-config", () => Results.Ok(new
{
    sdkVersion = typeof(ServiceOptions).Assembly.GetName().Version?.ToString(),
    enableAuthenticationRefresh = true,
    closeOnAuthenticationExpiration = false,
}));
app.MapHub<CompatibilityHub>("/hub", options =>
{
    options.EnableAuthenticationRefresh = true;
    options.CloseOnAuthenticationExpiration = false;
}).RequireAuthorization();

app.Run();

internal sealed class CompatibilityHub : Hub
{
    public object WhoAmIState() => new
    {
        Context.ConnectionId,
        Context.UserIdentifier,
        Role = Context.User?.FindFirst("role")?.Value,
        Marker = Context.User?.FindFirst("marker")?.Value,
    };
}

internal sealed record TokenRequest(
    string? UserId,
    string? Role,
    int? LifetimeSeconds = null,
    bool IncludeExpiration = true);
