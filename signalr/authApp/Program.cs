using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5100");

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
builder.Services.AddSignalR();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapPost("/token", (DemoTokenRequest request, AppTokenProvider tokens) =>
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