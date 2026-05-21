using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5100");

builder.Services
    .AddAuthentication(DemoBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoBearerAuthenticationHandler>(
        DemoBearerAuthenticationHandler.SchemeName,
        options => { });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<DemoJwtTokenService>();
builder.Services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
builder.Services.AddSignalR();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapPost("/token", (DemoTokenRequest request, DemoJwtTokenService tokens) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId)
        || string.IsNullOrWhiteSpace(request.TenantId)
        || string.IsNullOrWhiteSpace(request.Role))
    {
        return Results.BadRequest("userId, tenantId, and role are required.");
    }

    return Results.Ok(new
    {
        accessToken = tokens.CreateToken(request.UserId.Trim(), request.TenantId.Trim(), request.Role.Trim()),
        tokenType = "Bearer",
        expiresIn = 3600
    });
});
app.MapHub<ChatHub>("/hub").RequireAuthorization();

app.Run();

public sealed record DemoTokenRequest(string UserId, string TenantId, string Role);