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

    [Function("SampleConfig")]
    public IActionResult SampleConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sample-config")] HttpRequest request) =>
        new OkObjectResult(new
        {
            mode = "Serverless",
            serviceTransportType = Environment.GetEnvironmentVariable("Azure__SignalR__ServiceTransportType") ?? "Transient",
            scenarios = typeof(AuthRefreshScenario)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(field => field.GetRawConstantValue()),
        });

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

        var lifetime = body.LifetimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : AppTokenProvider.DefaultLifetime;
        var token = tokens.CreateToken(
            body.UserId.Trim(),
            body.Role.Trim(),
            NormalizeScenario(body.Scenario),
            lifetime,
            body.IncludeExpiration);
        var lifetimeSeconds = token.ExpiresAt is { } expiration
            ? Math.Max(0, (int)(expiration - DateTimeOffset.UtcNow).TotalSeconds)
            : (int?)null;

        return new OkObjectResult(new
        {
            accessToken = token.AccessToken,
            tokenType = "Bearer",
            expiresAt = token.ExpiresAt?.ToUnixTimeSeconds(),
            expiresIn = lifetimeSeconds,
            tokenLifetimeSeconds = lifetimeSeconds,
            scenario = NormalizeScenario(body.Scenario),
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

        if (app.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new UnauthorizedResult();
        }

        var hubContext = signalR.ChatHubContext;
        var options = BuildNegotiationOptions(app);
        try
        {
            if (app.Scenario == AuthRefreshScenario.OrdinaryNegotiate)
            {
                var ordinary = await hubContext.NegotiateAsync(options, request.HttpContext.RequestAborted);
                return new OkObjectResult(new { url = ordinary.Url, accessToken = ordinary.AccessToken });
            }

            var negotiate = await hubContext.NegotiateWithTokenLifetimeAsync(
                app.Scenario == AuthRefreshScenario.LifetimeApiNullOptions ? null! : options,
                request.HttpContext.RequestAborted);

            return new OkObjectResult(new
            {
                url = negotiate.Url,
                accessToken = negotiate.AccessToken,
                tokenLifetimeSeconds = negotiate.TokenLifetimeSeconds,
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new BadRequestObjectResult(new { error = "expired_authentication_deadline", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = "invalid_negotiation_options", detail = ex.Message });
        }
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

    [Function("Claims")]
    public async Task<IActionResult> Claims(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "hub/claims")] HttpRequest request)
    {
        var connectionToken = request.Query["id"].FirstOrDefault();
        if (string.IsNullOrEmpty(connectionToken))
        {
            return new BadRequestObjectResult(new { error = "missing_connection_token" });
        }

        if (!TryGetBearer(request, out var appToken) || !tokens.TryValidate(appToken, out _))
        {
            return new UnauthorizedResult();
        }

        try
        {
            var result = await signalR.ChatHubContext.GetConnectionClaimsAsync(
                connectionToken,
                request.HttpContext.RequestAborted);
            return new OkObjectResult(new
            {
                claims = result.Claims.Select(claim => new { claim.Type, claim.Value }),
            });
        }
        catch (AzureSignalRException ex)
        {
            return new ObjectResult(new { error = "claims_failed", detail = ex.Message })
            {
                StatusCode = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest,
            };
        }
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
            var options = BuildRefreshOptions(app);
            var result = await hubContext.RefreshConnectionAuthenticationAsync(
                connectionToken,
                options,
                request.HttpContext.RequestAborted);

            logger.LogInformation(
                "RefreshConnectionAuthenticationAsync(connectionToken={ConnectionToken}) returned tokenLifetimeSeconds={TokenLifetimeSeconds}, accessToken={AccessToken}",
                connectionToken,
                result.TokenLifetimeSeconds,
                "<redacted>");

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
        catch (ArgumentOutOfRangeException ex)
        {
            return new BadRequestObjectResult(new { error = "invalid_expiration", detail = ex.Message });
        }
    }

    private static NegotiationOptions BuildNegotiationOptions(AppPrincipal app)
    {
        var authenticationExpiresOn = app.Scenario switch
        {
            AuthRefreshScenario.RenewalOnly => null,
            AuthRefreshScenario.DeadlineOmitted => null,
            AuthRefreshScenario.DeadlineMaxValue => DateTimeOffset.MaxValue,
            AuthRefreshScenario.PastNegotiationDeadline => DateTimeOffset.UtcNow.AddSeconds(-1),
            AuthRefreshScenario.NonUtcExpiration => app.ExpiresAt?.ToOffset(TimeSpan.FromHours(8)),
            _ => app.ExpiresAt,
        };

        return new NegotiationOptions
        {
            UserId = app.UserId,
            Claims = BuildClaims(app),
            TokenLifetime = app.Scenario switch
            {
                AuthRefreshScenario.TokenMaximumWins => TimeSpan.FromSeconds(30),
                AuthRefreshScenario.RenewalOnly => TimeSpan.FromSeconds(60),
                _ => TimeSpan.FromHours(1),
            },
            AuthenticationExpiresOn = authenticationExpiresOn,
            EnableAuthenticationRefresh = app.Scenario is not (
                AuthRefreshScenario.LifetimeApiDisabled or AuthRefreshScenario.OrdinaryNegotiate),
            CloseOnAuthenticationExpiration = app.Scenario is not (
                AuthRefreshScenario.CloseDisabled
                or AuthRefreshScenario.CloseDisabledMaxValue
                or AuthRefreshScenario.RenewalOnly),
        };
    }

    private static RefreshConnectionAuthenticationOptions BuildRefreshOptions(AppPrincipal app)
    {
        var (expiration, claims) = app.Scenario switch
        {
            AuthRefreshScenario.ExpirationOnly => (app.ExpiresAt, null),
            AuthRefreshScenario.ClaimsOnly => (null, BuildClaims(app)),
            AuthRefreshScenario.RenewalOnly => (null, null),
            AuthRefreshScenario.NonUtcExpiration => (app.ExpiresAt?.ToOffset(TimeSpan.FromHours(8)), BuildClaims(app)),
            AuthRefreshScenario.MaxValueExpiration => (DateTimeOffset.MaxValue, BuildClaims(app)),
            AuthRefreshScenario.CloseDisabledMaxValue => (DateTimeOffset.MaxValue, BuildClaims(app)),
            AuthRefreshScenario.PastRefreshExpiration => (DateTimeOffset.UtcNow.AddSeconds(-1), BuildClaims(app)),
            _ => (app.ExpiresAt, BuildClaims(app)),
        };

        return new RefreshConnectionAuthenticationOptions
        {
            AuthenticationExpiresOn = expiration,
            Claims = claims,
            TokenLifetime = app.Scenario switch
            {
                AuthRefreshScenario.TokenMaximumWins => TimeSpan.FromSeconds(30),
                AuthRefreshScenario.RenewalOnly => TimeSpan.FromSeconds(60),
                _ => TimeSpan.FromHours(1),
            },
        };
    }

    // Claims put into the service token. NameIdentifier must equal the negotiate UserId so the runtime's
    // same-user enforcement on refresh passes; role/marker demonstrate claims flowing on refresh.
    private static List<Claim> BuildClaims(AppPrincipal app) =>
        new()
        {
            new Claim(ClaimTypes.NameIdentifier, app.UserId),
            new Claim("role", app.Role ?? "user"),
            new Claim("marker", app.Marker ?? Guid.NewGuid().ToString("N")),
            new Claim("scenario", app.Scenario),
        };

    private static string NormalizeScenario(string? scenario) =>
        string.IsNullOrWhiteSpace(scenario) ? AuthRefreshScenario.Standard : scenario.Trim().ToLowerInvariant();

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

        [JsonPropertyName("scenario")]
        public string? Scenario { get; init; }

        [JsonPropertyName("lifetimeSeconds")]
        public int? LifetimeSeconds { get; init; }

        [JsonPropertyName("includeExpiration")]
        public bool IncludeExpiration { get; init; } = true;
    }

    private sealed record BroadcastRequest
    {
        [JsonPropertyName("user")]
        public string? User { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
