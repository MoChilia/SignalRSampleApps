using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Protocol;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
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

        // Spec-conformant refresh-auth endpoint. Matches the customer-facing contract for Serverless mode:
        //
        //   POST /api/refresh?hub={hub}&id={connectionIdOrToken}&mode={transient|persistent}
        //   Authorization: Bearer {app-token}
        //
        //   200 OK { accessToken, tokenLifetimeSeconds, mode }   // refresh applied
        //   401  invalid / expired app token (JwtBearer middleware)
        //   404  ConnectionNotFound from ASRS
        //   500  CrossPodForwardFailed or other ASRS / app server error
        //
        // The sample exposes both Management SDK transports defined in spec §3:
        //
        //   mode=transient  (default) — one-shot HTTPS to {endpoint}/api/hubs/{hub}/connections/{id}/:refresh
        //                              signed with an HS256 token derived from the connection string's AccessKey.
        //   mode=persistent           — RefreshAuthMessage(ConnectionIdOrToken, ExpireTime, AckId) is written to the
        //                              SDK's existing persistent service connection and we await the AckMessage.
        //
        // In both cases the request converges on the runtime's `ClientConnectionLifetimeManager.RefreshClientAuthAsync`
        // (the same `case RefreshAuthMessage` in `SendToClientsAsync`): the REST controller
        // (`HubProxyV20260701Controller.RefreshConnectionAuth`) builds a `RefreshAuthMessage` and forwards it via the
        // message broker, and the persistent transport delivers the same message over the service connection. The
        // handler then calls `RefreshLocalClientAuthAsync` → `SignalRClientConnectionContext.TryRefreshAuthentication`,
        // which advances `AuthenticationExpiresOn` (and `CloseOnAuthExpirationFeature.ExpiresOn`) in place — no reconnect.
        // Default-mode SDK plumbing (Phase 3 in spec §8) is not yet shipped, so the persistent path here
        // reaches into the SDK's internal `IServiceConnectionManager<ChatHub>` via reflection and pushes
        // the message over the same service connection the SDK already uses for SendUserAsync/etc.
        app.MapPost("/api/refresh", async (
            HttpContext httpContext,
            AppTokenProvider tokens,
            IHttpClientFactory httpFactory,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            string? hub,
            string? id,
            string? mode,
            int? additionalSeconds) =>
        {
            var logger = loggerFactory.CreateLogger("ApiRefresh");
            var normalizedMode = (mode ?? "transient").Trim().ToLowerInvariant();
            if (normalizedMode is not ("transient" or "persistent"))
            {
                return Results.BadRequest(new
                {
                    error = $"Unsupported mode '{mode}'. Expected 'transient' or 'persistent'.",
                });
            }

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

            return normalizedMode switch
            {
                "persistent" => await RefreshViaPersistentAsync(
                    serviceProvider, logger, id!, newExpireTime, refreshedAppToken, httpContext.RequestAborted),
                _ => await RefreshViaTransientAsync(
                    httpFactory, logger, connectionString, hubName, id!, newExpireTime, refreshedAppToken, httpContext.RequestAborted),
            };
        }).RequireAuthorization();

        // Opt this hub's negotiate response into the CloseOnAuthExpiration claim so the
        // service arms its heartbeat-based abort at the JWT's exp. The refresh-auth REST
        // API extends that close time on the running connection.
        //
        // We capture the HttpConnectionDispatcherOptions instance so the browser can flip
        // `CloseOnAuthenticationExpiration` at runtime via /api/options/closeOnAuthExp.
        // The dispatcher reads this property on each negotiate, so the new value takes
        // effect on the next (re)connect — existing WebSockets keep their original arm state.
        HubOptionsState.DispatcherOptions = null;
        app.MapHub<ChatHub>(HubPath, options =>
        {
            options.CloseOnAuthenticationExpiration = HubOptionsState.CloseOnAuthenticationExpiration;
            HubOptionsState.DispatcherOptions = options;
        }).RequireAuthorization();

        // Read current value. Returns { closeOnAuthExp: bool }.
        app.MapGet("/api/options/closeOnAuthExp", () => Results.Ok(new
        {
            closeOnAuthExp = HubOptionsState.CloseOnAuthenticationExpiration,
        }));

        // Toggle/set CloseOnAuthenticationExpiration. Body: { "enabled": bool }.
        // The change applies to the NEXT negotiate; the browser must reconnect to observe it.
        app.MapPost("/api/options/closeOnAuthExp", (CloseOnAuthExpRequest request) =>
        {
            HubOptionsState.CloseOnAuthenticationExpiration = request.Enabled;
            if (HubOptionsState.DispatcherOptions is { } captured)
            {
                captured.CloseOnAuthenticationExpiration = request.Enabled;
            }
            return Results.Ok(new { closeOnAuthExp = HubOptionsState.CloseOnAuthenticationExpiration });
        });

        return app;
    }

    /// <summary>
    /// The route under which <see cref="ChatHub"/> is mapped. Shared with the integration test.
    /// </summary>
    public const string HubPath = "/hub";

    // Spec §3 — Transient (REST data plane).
    //
    //   1. App server mints a refreshed app token for the same identity (acting as the IdP
    //      stand-in for this sample) and uses its `exp` as the new `expireTime`.
    //   2. App server signs an HS256 service token (aud = the /:refresh resource URL)
    //      with the connection string's AccessKey.
    //   3. App server POSTs to the ASRS data-plane:
    //        {endpoint}/api/hubs/{hub}/connections/{id}/:refresh?api-version=2026-07-01
    //      with body { "expireTime": "<new utc>" }.
    //   4. On 204 No Content, the ASRS runtime has advanced AuthenticationExpiresOn for
    //      the live connection in place — no reconnect, no message loss.
    //   5. App server returns { accessToken, tokenLifetimeSeconds } so the SignalR client's
    //      accessTokenFactory has a fresh token for the next (re)connect.
    private static async Task<IResult> RefreshViaTransientAsync(
        IHttpClientFactory httpFactory,
        ILogger logger,
        string connectionString,
        string hubName,
        string connectionIdOrToken,
        DateTimeOffset newExpireTime,
        TokenResult refreshedAppToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var (endpoint, accessKey) = ParseConnectionString(connectionString);
            var resourceUrl = $"{endpoint.TrimEnd('/')}/api/hubs/{hubName}/connections/{Uri.EscapeDataString(connectionIdOrToken)}/:refresh";
            var requestUrl = $"{resourceUrl}?api-version=2026-07-01";
            var serviceToken = SignDataPlaneAccessToken(resourceUrl, accessKey);

            using var http = httpFactory.CreateClient();
            using var serviceRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = JsonContent.Create(new { expireTime = newExpireTime.UtcDateTime }),
            };
            serviceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

            logger.LogInformation("[transient] POST {Url} expireTime={Exp}", requestUrl, newExpireTime);
            using var serviceResponse = await http.SendAsync(serviceRequest, cancellationToken);
            var serviceBody = await serviceResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation("[transient] Service responded {Status} {Reason}: {Body}",
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
                    mode = "transient",
                    serviceStatus = (int)serviceResponse.StatusCode,
                    serviceBody,
                }, statusCode: mapped);
            }

            // Spec §2 / §3 — success response shape.
            return Results.Ok(BuildSuccessPayload("transient", refreshedAppToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[transient] /api/refresh failed");
            return Results.Json(new
            {
                error = ex.GetType().FullName,
                mode = "transient",
                message = ex.Message,
                stack = ex.StackTrace,
            }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // Spec §3 — Persistent (service-protocol tunnel).
    //
    // The app server reuses the SDK's existing persistent service connection for the hub and
    // writes a `RefreshAuthMessage` (type 41) onto it; the SDK assigns an `AckId`, awaits the
    // matching `AckMessage`, and reports success / NotFound / failure. The live ASRS connection's
    // `AuthenticationExpiresOn` is advanced in place — no client reconnect.
    //
    // SDK-side plumbing (Phase 3 of spec §8) is not yet shipped, so this helper reaches into the
    // SDK's internal `IServiceConnectionManager<ChatHub>` via reflection. The same APIs back
    // `SendUserAsync` and friends, so we are riding the existing server↔service tunnel.
    private static async Task<IResult> RefreshViaPersistentAsync(
        IServiceProvider serviceProvider,
        ILogger logger,
        string connectionIdOrToken,
        DateTimeOffset newExpireTime,
        TokenResult refreshedAppToken,
        CancellationToken cancellationToken)
    {
        try
        {
            // The Microsoft.Azure.SignalR SDK exposes the per-hub `IServiceConnectionManager<THub>`
            // as `internal`, so DI-registered as `typeof(IServiceConnectionManager<>) -> typeof(ServiceConnectionManager<>)`.
            // Resolve it by walking the SDK assembly for the open generic, close it on ChatHub, and pull it from DI.
            var sdkAssembly = typeof(ServiceOptions).Assembly; // Microsoft.Azure.SignalR
            var openManagerType = sdkAssembly.GetType("Microsoft.Azure.SignalR.IServiceConnectionManager`1", throwOnError: false)
                ?? throw new InvalidOperationException(
                    "Could not locate Microsoft.Azure.SignalR.IServiceConnectionManager`1 in the SDK assembly. "
                    + "The persistent-mode demo relies on an internal SDK type; this sample expects the SDK source under ../../../../azure-signalr.");

            var managerType = openManagerType.MakeGenericType(typeof(ChatHub));
            var manager = serviceProvider.GetService(managerType)
                ?? throw new InvalidOperationException(
                    $"DI did not return an instance of {managerType}. The persistent service connection for ChatHub is not registered.");

            // WriteAckableMessageAsync(ServiceMessage, CancellationToken) lives on the
            // internal IServiceMessageWriter interface but is declared `public` on the concrete
            // ServiceConnectionManager<THub>, so reflection over the runtime type sees it.
            var writeMethod = manager.GetType().GetMethod(
                "WriteAckableMessageAsync",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(ServiceMessage), typeof(CancellationToken) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    $"Could not find WriteAckableMessageAsync(ServiceMessage, CancellationToken) on {manager.GetType()}.");

            // Spec §4 — Service Protocol. Claims is null in v1 (expiration-only update).
            // AckId is assigned inside the SDK's WriteAckableMessageAsync; the value we pass is overwritten.
            var message = new RefreshAuthMessage(
                connectionIdOrToken: connectionIdOrToken,
                claims: null,
                expireTime: newExpireTime,
                ackId: 0);

            logger.LogInformation("[persistent] WriteAckableMessageAsync RefreshAuthMessage id=<{IdKind}> expireTime={Exp}",
                connectionIdOrToken.Length > 16 ? "connectionToken" : "connectionId", newExpireTime);

            var task = (Task<bool>)writeMethod.Invoke(manager, new object?[] { message, cancellationToken })!;
            var ok = await task.ConfigureAwait(false);

            if (!ok)
            {
                // Spec §7 — AckStatus.NotFound surfaces as `false` from the ackable write.
                logger.LogInformation("[persistent] Service replied NotFound (AckStatus.NotFound) for id=<{IdKind}>",
                    connectionIdOrToken.Length > 16 ? "connectionToken" : "connectionId");
                return Results.Json(new
                {
                    error = "RefreshAuthFailed",
                    mode = "persistent",
                    serviceStatus = StatusCodes.Status404NotFound,
                    serviceBody = "ConnectionNotFound (AckStatus.NotFound)",
                }, statusCode: StatusCodes.Status404NotFound);
            }

            logger.LogInformation("[persistent] AckStatus.Ok");
            return Results.Ok(BuildSuccessPayload("persistent", refreshedAppToken));
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            return PersistentFailure(logger, tie.InnerException);
        }
        catch (Exception ex)
        {
            return PersistentFailure(logger, ex);
        }
    }

    private static IResult PersistentFailure(ILogger logger, Exception ex)
    {
        logger.LogError(ex, "[persistent] /api/refresh failed");
        return Results.Json(new
        {
            error = ex.GetType().FullName,
            mode = "persistent",
            message = ex.Message,
            stack = ex.StackTrace,
        }, statusCode: StatusCodes.Status500InternalServerError);
    }

    private static object BuildSuccessPayload(string mode, TokenResult refreshedAppToken) => new
    {
        accessToken = refreshedAppToken.AccessToken,
        tokenLifetimeSeconds = (int)(refreshedAppToken.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds,
        mode,
    };

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

public sealed record CloseOnAuthExpRequest(bool Enabled);

// Holds the currently effective `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration`
// and a reference to the live options instance so the /api/options endpoint can mutate it in place.
internal static class HubOptionsState
{
    public static bool CloseOnAuthenticationExpiration { get; set; } = true;
    public static HttpConnectionDispatcherOptions? DispatcherOptions { get; set; }
}
