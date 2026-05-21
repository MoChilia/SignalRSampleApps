using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using System.Security.Claims;

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
    .AddAuthentication(DemoBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoBearerAuthenticationHandler>(
        DemoBearerAuthenticationHandler.SchemeName,
        options => { });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<AppTokenProvider>();
builder.Services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
builder.Services.AddSignalR().AddAzureSignalR(options =>
{
    options.ConnectionString = connectionString;
    options.ClaimsProvider = context =>
    context.User.FindFirst(ClaimTypes.NameIdentifier) is { } userId
        ? new[] { userId }
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

    return Results.Ok(new
    {
        accessToken = tokens.CreateToken(request.UserId.Trim(), request.Role.Trim()),
        tokenType = "Bearer",
        expiresIn = 3600
    });
});
app.MapHub<ChatHub>("/hub").RequireAuthorization();

app.Run();

public sealed record DemoTokenRequest(string UserId, string Role);