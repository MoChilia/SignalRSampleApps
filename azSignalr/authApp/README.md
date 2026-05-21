# Azure SignalR auth sample

This sample is the Azure SignalR Service version of the self-hosted auth app. It shows this flow:

1. The browser posts user and role values to `/token`.
2. The app token provider returns a signed demo JWT.
3. The browser sends that JWT to `/hub` with `accessTokenFactory`.
4. ASP.NET Core authenticates the JWT and creates a `ClaimsPrincipal`.
5. `IUserIdProvider` maps `ClaimTypes.NameIdentifier` to the SignalR user id.
6. Azure SignalR Service hosts the client connection, and the hub can call `Clients.User(userId)`.

Configure the Azure SignalR connection string:

```powershell
dotnet user-secrets set "Azure:SignalR:ConnectionString" "<your connection string>"
```

Or set the environment variable:

```powershell
$env:Azure__SignalR__ConnectionString = "<your connection string>"
```

Run the app:

```powershell
dotnet run --project AzureSignalRAuthSample.csproj
```

Open the browser sample:

```text
http://localhost:5120
```

Open two browser windows, connect one as `alice` and one as `bob`, then use `Send to user` to target either user id.

The browser first requests a JWT:

```powershell
Invoke-RestMethod http://localhost:5120/token -Method Post -ContentType "application/json" -Body '{"userId":"alice","role":"operator"}'
```

The returned `accessToken` is an HS256 JWT with `sub`, `name`, and the .NET role claim type. The SignalR JavaScript client sends it to `/hub/negotiate`; the Azure SignalR SDK includes the authenticated claims in the service token used by Azure SignalR Service.

This sample is for identity mechanics only. A production app should replace the demo token endpoint and `DemoBearerAuthenticationHandler` with a real authentication scheme such as cookie auth, OpenID Connect, or JWT bearer validation.
