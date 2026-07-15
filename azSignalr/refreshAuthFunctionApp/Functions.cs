// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.SignalR.Common;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Logging;

namespace RefreshAuthFunctionApp;

/// <summary>
/// HTTP-triggered functions that make this Function App the serverless auth boundary for Azure SignalR.
/// Routes use an empty prefix (see host.json) so the endpoints line up with what the .NET SignalR
/// client appends to its hub URL:
///   POST /token           -> mint a demo app token (what the client's AccessTokenProvider fetches)
///   POST /hub/negotiate    -> validate app token, ServiceHubContext.NegotiateAsync -> ASRS redirect
///   POST /hub/refresh      -> validate new app token, ServiceHubContext.RefreshConnectionAuthenticationAsync -> new token
/// Point refreshAuthClient at this app's base URL (e.g. http://localhost:7071) and it works unchanged.
/// </summary>
public sealed class RefreshAuthFunctions(SignalRService signalR, AppTokenProvider tokens, ILogger<RefreshAuthFunctions> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("Token")]
    public async Task<IActionResult> Token(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "token")] HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<TokenRequest>(request.Body, JsonOptions)
            ?? new TokenRequest();

        if (string.IsNullOrWhiteSpace(body.UserId) || string.IsNullOrWhiteSpace(body.Role))
        {
            return new BadRequestObjectResult(new { error = "userId and role are required." });
        }

        var token = tokens.CreateToken(body.UserId.Trim(), body.Role.Trim());
        var lifetimeSeconds = (int)Math.Max(0, (token.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);

        return new OkObjectResult(new
        {
            accessToken = token.AccessToken,
            tokenType = "Bearer",
            expiresIn = lifetimeSeconds,
            tokenLifetimeSeconds = lifetimeSeconds,
        });
    }

    [Function("Negotiate")]
    public async Task<IActionResult> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hub/negotiate")] HttpRequest request)
    {
        if (!TryGetBearer(request, out var appToken) || !tokens.TryValidate(appToken, out var app))
        {
            return new UnauthorizedResult();
        }

        var lifetime = app.ExpiresAt - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            return new UnauthorizedResult();
        }

        var hubContext = signalR.ChatHubContext;
        var negotiate = await hubContext.NegotiateWithTokenLifetimeAsync(
            new NegotiationOptions
            {
                UserId = app.UserId,
                Claims = BuildClaims(app),
                // Align the service-token lifetime with the app token so the connection's auth deadline tracks the
                // app token; CloseOnAuthenticationExpiration aborts it if refresh does not happen. The advertised
                // tokenLifetimeSeconds equals TokenLifetime (= app-token remaining lifetime), computed by the SDK.
                TokenLifetime = lifetime,
                CloseOnAuthenticationExpiration = true,
            },
            request.HttpContext.RequestAborted);

        // Standard SignalR redirect-negotiate response. tokenLifetimeSeconds (computed by the SDK) tells the
        // client when to schedule its refresh (mirrors Default mode).
        return new OkObjectResult(new
        {
            url = negotiate.Url,
            accessToken = negotiate.AccessToken,
            tokenLifetimeSeconds = negotiate.TokenLifetimeSeconds,
        });
    }

    [Function("Broadcast")]
    public async Task<IActionResult> Broadcast(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hub/broadcast")] HttpRequest request)
    {
        var body = await JsonSerializer.DeserializeAsync<BroadcastRequest>(request.Body, JsonOptions)
            ?? new BroadcastRequest();

        if (string.IsNullOrWhiteSpace(body.Message))
        {
            return new BadRequestObjectResult(new { error = "message is required." });
        }

        var hubContext = signalR.ChatHubContext;

        await hubContext.Clients.All.SendAsync(
            "ReceiveMessage",
            string.IsNullOrWhiteSpace(body.User) ? "server" : body.User.Trim(),
            body.Message.Trim(),
            request.HttpContext.RequestAborted);

        return new OkObjectResult(new { broadcast = true });
    }

    [Function("Refresh")]
    public async Task<IActionResult> Refresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hub/refresh")] HttpRequest request)
    {
        var connectionToken = request.Query["id"].FirstOrDefault();
        if (string.IsNullOrEmpty(connectionToken))
        {
            return new BadRequestObjectResult(new { error = "missing_connection_token" });
        }

        if (!TryGetBearer(request, out var appToken) || !tokens.TryValidate(appToken, out var app))
        {
            return new UnauthorizedResult();
        }

        var hubContext = signalR.ChatHubContext;

        // Serverless equivalent of Default mode's OnAuthenticationRefresh gate: inspect the current
        // connection claims (PreviousUser) and decide whether to allow the refresh. Here we reject a
        // demoted user (new token carries role "blocked").
        if (string.Equals(app.Role, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            var claimsResult = await hubContext.GetConnectionClaimsAsync(connectionToken, request.HttpContext.RequestAborted);
            var claims = claimsResult?.Claims;
            logger.LogInformation(
                "GetConnectionClaimsAsync(connectionToken={ConnectionToken}) returned {Count} claim(s): {Claims}",
                connectionToken,
                claims?.Count ?? 0,
                claims is null ? "<null>" : string.Join(", ", claims.Select(c => $"{c.Type}={c.Value}")));
            return new ObjectResult(new { error = "permission_change_rejected" })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        try
        {
            var result = await hubContext.RefreshConnectionAuthenticationAsync(
                connectionToken,
                app.ExpiresAt,
                BuildClaims(app),
                request.HttpContext.RequestAborted);

            logger.LogInformation(
                "RefreshConnectionAuthenticationAsync(connectionToken={ConnectionToken}) returned tokenLifetimeSeconds={TokenLifetimeSeconds}, accessToken={AccessToken}",
                connectionToken,
                result.TokenLifetimeSeconds,
                result.AccessToken);

            logger.LogInformation(
                "Refreshed service access token payload: {Payload}",
                DecodeJwtPayload(result.AccessToken));

            return new OkObjectResult(new
            {
                accessToken = result.AccessToken,
                tokenLifetimeSeconds = result.TokenLifetimeSeconds,
            });
        }
        catch (AzureSignalRException ex)
        {
            // The Management SDK surfaces refresh failures (unknown connection, user mismatch, service
            // errors) as AzureSignalRException. Best-effort status mapping for the demo.
            var status = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status404NotFound
                : ex.Message.Contains("different user", StringComparison.OrdinalIgnoreCase)
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status400BadRequest;

            return new ObjectResult(new { error = "refresh_failed", detail = ex.Message })
            {
                StatusCode = status,
            };
        }
    }

    // Claims put into the service token. NameIdentifier must equal the negotiate UserId so the runtime's
    // same-user enforcement on refresh passes; role/marker demonstrate claims flowing on refresh.
    private static List<Claim> BuildClaims(AppPrincipal app) =>
        new()
        {
            new Claim(ClaimTypes.NameIdentifier, app.UserId),
            new Claim("role", app.Role ?? "user"),
            new Claim("marker", app.Marker ?? Guid.NewGuid().ToString("N")),
        };

    private static bool TryGetBearer(HttpRequest request, out string? token)
    {        token = null;
        var header = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header["Bearer ".Length..].Trim();
        return token.Length > 0;
    }

    // Decodes the payload (middle segment) of a JWT for debug logging only. No signature/expiry
    // validation is performed here; this is purely to inspect the refreshed service token's claims.
    private static string DecodeJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<empty>";
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return "<not a jwt>";
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return json;
        }
        catch (FormatException)
        {
            return "<undecodable>";
        }
    }

    private sealed record TokenRequest
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }
    }

    private sealed record BroadcastRequest
    {
        [JsonPropertyName("user")]
        public string? User { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
