# Refresh Auth TypeScript Client

This Node-based E2E client exercises authentication refresh through the locally built
`@microsoft/signalr` TypeScript client against `../refreshAuthApp`.

Build the local ASP.NET Core TypeScript client package first, then install and build this client:

```powershell
# Run from the aspnetcore TypeScript client root.
Set-Location <aspnetcore-root>\src\SignalR\clients\ts
npm install
npm install --no-save --package-lock=false @types/jasmine@5.1.0
npm run prebuild --prefix .\signalr
npm run build:esm --prefix .\signalr
npm run build:cjs --prefix .\signalr

# Run from this directory.
Set-Location <SignalRSampleApps-root>\azSignalr\refreshAuth\refreshAuthTsClient
npm pack <aspnetcore-root>\src\SignalR\clients\ts\signalr
npm install --no-save .\microsoft-signalr-11.0.0-dev.tgz
npm run build
node dist/index.js --server-url http://localhost:5121 --transport ws
```

The client obtains an application token from `/token`, connects through the Azure SignalR
negotiate redirect, calls `refreshAuthentication()`, and verifies that a post-refresh hub
invocation succeeds without changing the connection ID. This default one-shot mode is used by
the E2E matrix. It also verifies that the cached Azure SignalR service token changes and that its
JWT expiration advances.

## Interactive automatic refresh

Start the client in interactive mode with automatic refresh enabled:

```powershell
node dist/index.js `
	--server-url http://localhost:5121 `
	--transport ws `
	--interactive on `
	--auto-refresh on `
	--lifetime-seconds 60 `
	--refresh-before-ms 20000
```

With a 60-second token lifetime and a 20-second refresh lead time, the automatic refresh should
normally occur about 40 seconds after connection. The client prints a `[refresh:event]` line with
the refreshed lifetime, whether the cached service token changed, and the new service-token JWT
expiration. Use `/status` before and after the event to confirm that the connection ID remains the
same and the token expiration advances.

Interactive commands:

- `/refresh`: request an immediate manual authentication refresh.
- `/marker`: print the current server-side application-principal marker claim.
- `/status`: print connection ID, marker, application expiration, service-token expiration, and
	automatic-refresh state.
- `/help`: print the command list.
- `/quit`: stop the client.

The `--auto-refresh`, `--interactive`, `--lifetime-seconds`, and `--refresh-before-ms` options are
optional. Both switches default to `off`; lifetime defaults to 60 seconds and refresh lead time
defaults to 20,000 milliseconds. If the token lifetime is no more than twice the lead time, the
SignalR client schedules refresh at half of the token lifetime.

`run-refresh-auth-e2e-matrix.ps1` performs the same bootstrap and adds this client to the
Default-mode matrix. It writes the temporary SignalR tarball under `.e2e-artifacts`, so the
checked-out SignalR source is never installed as a junction. The local ASP.NET Core TypeScript
client dependencies must be installable from the configured npm registry before the matrix can
run the TypeScript case.
