# SignalR auth sample

This sample shows the recommended identity flow for `Clients.User(...)`:

1. The browser posts user, tenant, and role values to `/token`.
2. The server returns a signed demo JWT.
3. The browser sends that JWT to SignalR with `accessTokenFactory`.
4. ASP.NET Core JWT bearer authentication validates the token and creates a `ClaimsPrincipal`.
5. `IUserIdProvider` reads `ClaimTypes.NameIdentifier` from that principal.
6. The hub uses `Clients.User(userId)` to target authenticated users.

The browser first requests a JWT:

```powershell
Invoke-RestMethod http://localhost:5100/token -Method Post -ContentType "application/json" -Body '{"userId":"alice","tenantId":"contoso","role":"operator"}'
```

The returned `accessToken` is an HS256 JWT with `sub`, `name`, `tenant_id`, and `role` claims. ASP.NET Core JWT bearer authentication validates it from `Bearer <token>` or, for browser WebSocket/SSE transports, from the `access_token` query value.

Run the app:

```powershell
dotnet run --project SignalRAuthSample.csproj
```

Open the browser sample:

```text
http://localhost:5100
```

Open two browser windows, connect one as `alice` and one as `bob`, then use `Send to user` to target either user id.

This sample is for identity mechanics only. A production app should replace the demo token endpoint with a real identity provider such as Microsoft Entra ID, OpenID Connect, or another JWT issuer.
