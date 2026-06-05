using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        builder.Services.AddHttpClient();
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
        // we advertise `tokenLifetimeSeconds` so the client knows when to call /api/refresh.
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

        // Spec-conformant refresh-auth endpoint. Matches the customer-facing contract fro for Serverless mode:
        //
        //   POST /api/refresh?hub={hub}&id={connectionIdOrToken}
        //   Authorization: Bearer {app-token}
        //
        //   200 OK { accessToken, tokenLifetimeSeconds }   // refresh applied
        //   401  invalid / expired app token (JwtBearer middleware)
        //   404  ConnectionNotFound from ASRS
        //   500  CrossPodForwardFailed or other ASRS / app server error
        //
        // Flow (Phase 1):
        //   1. Client sends current app token in Authorization header. JwtBearer validates it.
        //   2. App server mints a refreshed app token for the same identity (acting as the IdP
        //      stand-in for this sample) and uses its `exp` as the new `expireTime`.
        //   3. App server signs an HS256 service token (aud = the /:refresh resource URL)
        //      with the connection string's AccessKey.
        //   4. App server POSTs to the ASRS data-plane:
        //        {endpoint}/api/hubs/{hub}/connections/{id}/:refresh?api-version=2026-07-01
        //      with body { "expireTime": "<new utc>" }. (Spec §3 — Management SDK Transport)
        //   5. On 204 No Content, the ASRS runtime has advanced AuthenticationExpiresOn for
        //      the live connection in place — no reconnect, no message loss.
        //   6. App server returns { accessToken, tokenLifetimeSeconds } so the SignalR client's
        //      accessTokenFactory has a fresh token for the next (re)connect.
        app.MapPost("/api/refresh", async (
            HttpContext httpContext,
            AppTokenProvider tokens,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            string? hub,
            string? id,
            int? additionalSeconds) =>
        {
            var logger = loggerFactory.CreateLogger("ApiRefresh");

            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest(new { error = "Query parameter 'id' (connectionIdOrToken) is required." });
            }

            // Spec §3: Function App stand-in for the IdP. We mint a refreshed app token for the
            // already-validated identity; its `exp` becomes the new ASRS expireTime. In a real
            // serverless setup the client would obtain this token from its IdP and pass it here.
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var role = httpContext.User.FindFirst("role")?.Value ?? "user";
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var lifetime = additionalSeconds is int s and > 0
                ? TimeSpan.FromSeconds(s)
                : AppTokenProvider.DefaultLifetime;
            var refreshedAppToken = tokens.CreateToken(userId, role, lifetime);
            var newExpireTime = refreshedAppToken.ExpiresAt;

            // Spec §3 — hub name in the REST path uses the service-side bucket, which the
            // server SDK lowercases (DefaultServiceEndpointGenerator.GetPrefixedHubName).
            var hubName = (string.IsNullOrWhiteSpace(hub) ? nameof(ChatHub) : hub!).ToLowerInvariant();

            try
            {
                var (endpoint, accessKey) = ParseConnectionString(connectionString);
                var resourceUrl = $"{endpoint.TrimEnd('/')}/api/hubs/{hubName}/connections/{Uri.EscapeDataString(id)}/:refresh";
                var requestUrl = $"{resourceUrl}?api-version=2026-07-01";
                var serviceToken = SignDataPlaneAccessToken(resourceUrl, accessKey);

                using var http = httpFactory.CreateClient();
                using var serviceRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = JsonContent.Create(new { expireTime = newExpireTime.UtcDateTime }),
                };
                serviceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

                logger.LogInformation("POST {Url} expireTime={Exp}", requestUrl, newExpireTime);
                using var serviceResponse = await http.SendAsync(serviceRequest);
                var serviceBody = await serviceResponse.Content.ReadAsStringAsync();
                logger.LogInformation("Service responded {Status} {Reason}: {Body}",
                    (int)serviceResponse.StatusCode, serviceResponse.ReasonPhrase, serviceBody);

                // Spec §7 — typed failure mapping.
                if (!serviceResponse.IsSuccessStatusCode)
                {
                    var mapped = (int)serviceResponse.StatusCode switch
                    {
                        401 or 403 => StatusCodes.Status401Unauthorized,
                        404 => StatusCodes.Status404NotFound,   // ConnectionNotFound
                        _ => StatusCodes.Status500InternalServerError, // CrossPodForwardFailed / other
                    };
                    return Results.Json(new
                    {
                        error = "RefreshAuthFailed",
                        serviceStatus = (int)serviceResponse.StatusCode,
                        serviceBody,
                    }, statusCode: mapped);
                }

                // Spec §2 / §3 — success response shape.
                return Results.Ok(new
                {
                    accessToken = refreshedAppToken.AccessToken,
                    tokenLifetimeSeconds = (int)(refreshedAppToken.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds,
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "/api/refresh failed");
                return Results.Json(new
                {
                    error = ex.GetType().FullName,
                    message = ex.Message,
                    stack = ex.StackTrace,
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization();

        // Opt this hub's negotiate response into the CloseOnAuthExpiration claim so the
        // service arms its heartbeat-based abort at the JWT's exp. The refresh-auth REST
        // API extends that close time on the running connection.
        app.MapHub<ChatHub>(HubPath, options =>
        {
            options.CloseOnAuthenticationExpiration = true;
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The route under which <see cref="ChatHub"/> is mapped. Shared with the integration test.
    /// </summary>
    public const string HubPath = "/hub";

    private static (string Endpoint, string AccessKey) ParseConnectionString(string connectionString)
    {
        string? endpoint = null;
        string? accessKey = null;
        string? port = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            var name = part.Substring(0, eq).Trim();
            var value = part.Substring(eq + 1).Trim();
            if (name.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = value;
            }
            else if (name.Equals("AccessKey", StringComparison.OrdinalIgnoreCase))
            {
                accessKey = value;
            }
            else if (name.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                port = value;
            }
        }
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(accessKey))
        {
            throw new InvalidOperationException(
                "Connection string must contain Endpoint=... and AccessKey=... (AAD-only connection strings are not supported by the /api/refresh endpoint).");
        }

        // The Azure SignalR connection string for locally-hosted runtimes/emulators puts the
        // server port in a separate `Port=` segment (Endpoint=http://localhost;Port=8888;...).
        // Merge it into the endpoint URL so the data-plane REST call hits the right port
        // instead of defaulting to 80/443.
        if (!string.IsNullOrEmpty(port))
        {
            var builder = new UriBuilder(endpoint!) { Port = int.Parse(port!) };
            endpoint = builder.Uri.GetLeftPart(UriPartial.Authority);
        }

        return (endpoint!, accessKey!);
    }

    private static string SignDataPlaneAccessToken(string audience, string accessKey)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(accessKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            SigningCredentials = credentials,
        });
    }
}

public sealed record DemoTokenRequest(string UserId, string Role);
