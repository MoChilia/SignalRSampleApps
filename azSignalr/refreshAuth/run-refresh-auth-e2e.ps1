[CmdletBinding()]
param(
    [string] $ServerUrl = "http://localhost:5121",
    [string] $FunctionUrl = "http://localhost:7071",
    [string] $DotNetPath,
    [string] $NodePath = "node",
    [string] $DefaultConnectionString = $env:DEFAULT_SIGNALR_CONNECTION_STRING,
    [string] $ServerlessConnectionString = $env:SERVERLESS_SIGNALR_CONNECTION_STRING,
    [int] $TimeoutSeconds = 90,
    [switch] $SkipBuild,
    [switch] $SkipDefaultMode,
    [switch] $SkipServerlessMode
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
$hostProject = Join-Path $scriptRoot "refreshAuthApp\AzureSignalRRefreshAuthSample.csproj"
$functionProject = Join-Path $scriptRoot "refreshAuthFunctionApp\refreshAuthFunctionApp.csproj"
$functionDirectory = Split-Path $functionProject -Parent
$functionOutputDirectory = Join-Path $functionDirectory "bin\Debug\net8.0"
$clientProject = Join-Path $scriptRoot "refreshAuthClient\refreshAuthClient.csproj"
$typeScriptClientDirectory = Join-Path $scriptRoot "refreshAuthTsClient"
$aspNetCoreRoot = Join-Path $env:USERPROFILE "aspnetcore"
$signalRTypeScriptClientDirectory = Join-Path $aspNetCoreRoot "src\SignalR\clients\ts\signalr"
$artifactsDirectory = Join-Path $scriptRoot ".e2e-artifacts"
$restoreSources = @(
    "https://packagefeedproxy.microsoft.io/nuget/v3/index.json"
    (Join-Path $aspNetCoreRoot "artifacts\packages\Debug\Shipping")
) -join ";"

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = "dotnet"
}

function Invoke-DotNetBuild {
    param([string] $Project)

    & $DotNetPath build $Project --nologo "-p:RestoreSources=$restoreSources" /p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $Project (exit code $LASTEXITCODE)."
    }
}

function Assert-Match {
    param(
        [string] $Name,
        [string] $Text,
        [string] $Pattern,
        [int] $MinimumCount = 1
    )

    $count = ([regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)).Count
    if ($count -lt $MinimumCount) {
        throw "Assertion failed: $Name. Expected at least $MinimumCount match(es) for '$Pattern', found $count."
    }
    Write-Host "  PASS  $Name"
}

function Wait-ForHost {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $BaseUrl,
        [string] $ExpectedMode
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Host exited before becoming ready (exit code $($Process.ExitCode))."
        }
        try {
            $response = Invoke-RestMethod -Uri "$BaseUrl/sample-config" -TimeoutSec 2
            if ($ExpectedMode -eq "Default" -and $response.enableAuthenticationRefresh -eq $true) {
                return
            }
            if ($ExpectedMode -eq "Serverless" -and $response.mode -eq "Serverless") {
                return
            }
            throw "$ExpectedMode host returned an unexpected sample configuration."
        }
        catch [System.Net.WebException] {
            Start-Sleep -Milliseconds 250
        }
        catch [System.Net.Http.HttpRequestException] {
            Start-Sleep -Milliseconds 250
        }
        catch [System.Threading.Tasks.TaskCanceledException] {
            Start-Sleep -Milliseconds 250
        }
    }
    throw "$ExpectedMode host did not become ready at $BaseUrl within $TimeoutSeconds seconds."
}

function Wait-ForServerlessService {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $BaseUrl
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $tokenBody = @{
        userId = "e2e-readiness"
        role = "user"
        lifetimeSeconds = 60
        includeExpiration = $true
        scenario = "standard"
    } | ConvertTo-Json

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Serverless host exited while waiting for its service connection (exit code $($Process.ExitCode))."
        }
        try {
            $token = Invoke-RestMethod -Method Post -Uri "$BaseUrl/token" -ContentType "application/json" -Body $tokenBody -TimeoutSec 5
            $headers = @{ Authorization = "Bearer $($token.accessToken)" }
            $negotiate = Invoke-RestMethod -Method Post -Uri "$BaseUrl/hub/negotiate" -Headers $headers -TimeoutSec 5
            if (-not [string]::IsNullOrWhiteSpace($negotiate.accessToken)) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Serverless service connection did not become ready at $BaseUrl within $TimeoutSeconds seconds."
}

function Invoke-TypeScriptClientCase {
    param(
        [string] $Name,
        [string] $BaseUrl,
        [string] $Transport = "ws"
    )

    Write-Host "CASE  $Name"
    $stdout = Join-Path $artifactsDirectory "$Name.client.stdout.log"
    $stderr = Join-Path $artifactsDirectory "$Name.client.stderr.log"
    $process = Start-Process -FilePath $NodePath -WorkingDirectory $typeScriptClientDirectory -ArgumentList @(
        "dist/index.js",
        "--server-url", $BaseUrl,
        "--user-id", "alice",
        "--role", "user",
        "--transport", $Transport
    ) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "TypeScript client timed out after $TimeoutSeconds seconds"
        }
        if ($process.ExitCode -ne 0) {
            $detail = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { "No stderr log was written." }
            throw "TypeScript client exited with code $($process.ExitCode).`n$detail"
        }
        return Get-Content $stdout -Raw
    }
    finally {
        Stop-ProcessTree $process
    }
}

function Stop-ProcessTree {
    param([System.Diagnostics.Process] $Process)

    if ($null -ne $Process -and -not $Process.HasExited) {
        & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
    }
}

function Assert-DefaultConfiguration {
    if (-not [string]::IsNullOrWhiteSpace($DefaultConnectionString)) {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($env:Azure__SignalR__ConnectionString)) {
        return
    }

    $secretKeys = & $DotNetPath user-secrets list --project $hostProject 2>$null |
        ForEach-Object { ($_ -split " = ", 2)[0].Trim() }
    if ($secretKeys -contains "Azure:SignalR:ConnectionString") {
        return
    }

    throw @"
Default mode requires an Azure SignalR connection string. Configure one before running the script:

  dotnet user-secrets set "Azure:SignalR:ConnectionString" "<connection-string>" --project "$hostProject"

Alternatively, pass -DefaultConnectionString or set DEFAULT_SIGNALR_CONNECTION_STRING in the current PowerShell session.
"@
}

New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
$hostOutput = Join-Path $artifactsDirectory "default-host.stdout.log"
$hostError = Join-Path $artifactsDirectory "default-host.stderr.log"
$clientOutput = Join-Path $artifactsDirectory "default-client.stdout.log"
$clientError = Join-Path $artifactsDirectory "default-client.stderr.log"
$clientInput = Join-Path $artifactsDirectory "default-client.stdin.txt"
$functionOutput = Join-Path $artifactsDirectory "serverless-host.stdout.log"
$functionError = Join-Path $artifactsDirectory "serverless-host.stderr.log"
$serverlessClientOutput = Join-Path $artifactsDirectory "serverless-client.stdout.log"
$serverlessClientError = Join-Path $artifactsDirectory "serverless-client.stderr.log"
$serverlessClientInput = Join-Path $artifactsDirectory "serverless-client.stdin.txt"
Remove-Item $hostOutput, $hostError, $clientOutput, $clientError, $clientInput,
    $functionOutput, $functionError, $serverlessClientOutput, $serverlessClientError,
    $serverlessClientInput -Force -ErrorAction SilentlyContinue

$commands = @(
    "/status"
    "/marker"
    "/refresh"
    "/marker"
    "/status"
    "/refresh-as alice blocked"
    "/refresh-as bob user"
    "/probe get"
    "/probe missing-header"
    "/probe query-only"
    "/probe empty-header"
    "/probe no-auth"
    "/probe invalid-auth"
    "/probe unknown"
    "/probe header-and-query"
)
Set-Content -Path $clientInput -Value $commands -Encoding utf8

$serverlessCommands = @(
    "/claims"
    "/refresh"
    "/claims"
    "/refresh-as alice blocked"
    "/refresh-as bob user"
    "/probe missing-header"
    "/probe query-only"
    "/probe empty-header"
    "/probe no-auth"
    "/probe invalid-auth"
    "/probe header-and-query"
)
Set-Content -Path $serverlessClientInput -Value $serverlessCommands -Encoding utf8

$hostProcess = $null
$clientProcess = $null
$functionProcess = $null
$serverlessClientProcess = $null
try {
    if (-not $SkipDefaultMode) {
        Assert-DefaultConfiguration
    }

    if (-not $SkipBuild) {
        Write-Host "Building refresh-auth samples..."
        if (-not $SkipDefaultMode) {
            Invoke-DotNetBuild $hostProject
        }
        if (-not $SkipServerlessMode) {
            Invoke-DotNetBuild $functionProject
        }
        Invoke-DotNetBuild $clientProject
        & npm.cmd install --prefix (Split-Path $signalRTypeScriptClientDirectory -Parent)
        if ($LASTEXITCODE -ne 0) { throw "npm install failed for the local SignalR TypeScript SDK." }
        & npm.cmd install --prefix (Split-Path $signalRTypeScriptClientDirectory -Parent) --no-save --package-lock=false "@types/jasmine@5.1.0"
        if ($LASTEXITCODE -ne 0) { throw "npm install failed for the local SignalR TypeScript SDK Jasmine types." }
        & npm.cmd run prebuild --prefix $signalRTypeScriptClientDirectory
        if ($LASTEXITCODE -ne 0) { throw "npm run prebuild failed for the local SignalR TypeScript SDK." }
        & npm.cmd run build:esm --prefix $signalRTypeScriptClientDirectory
        if ($LASTEXITCODE -ne 0) { throw "npm run build:esm failed for the local SignalR TypeScript SDK." }
        & npm.cmd run build:cjs --prefix $signalRTypeScriptClientDirectory
        if ($LASTEXITCODE -ne 0) { throw "npm run build:cjs failed for the local SignalR TypeScript SDK." }
        $signalRPackageDirectory = Join-Path $artifactsDirectory "signalr-package"
        New-Item -ItemType Directory -Force -Path $signalRPackageDirectory | Out-Null
        Push-Location $signalRTypeScriptClientDirectory
        try {
            $signalRPackageName = (& npm.cmd pack --pack-destination $signalRPackageDirectory | Select-Object -Last 1).Trim()
        }
        finally {
            Pop-Location
        }
        $signalRPackage = Join-Path $signalRPackageDirectory $signalRPackageName
        & npm.cmd install --prefix $typeScriptClientDirectory
        if ($LASTEXITCODE -ne 0) { throw "npm install failed for $typeScriptClientDirectory." }
        & npm.cmd install --prefix $typeScriptClientDirectory --no-save $signalRPackage
        if ($LASTEXITCODE -ne 0) { throw "npm install failed for $typeScriptClientDirectory." }
        & npm.cmd run build --prefix $typeScriptClientDirectory
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed for $typeScriptClientDirectory." }
    }

    if (-not $SkipDefaultMode) {
        Write-Host "Starting Default-mode host at $ServerUrl..."
        $environment = @{ ASPNETCORE_ENVIRONMENT = "Development" }
        if (-not [string]::IsNullOrWhiteSpace($DefaultConnectionString)) {
            $environment.Azure__SignalR__ConnectionString = $DefaultConnectionString
        }
        $hostProcess = Start-Process -FilePath $DotNetPath -ArgumentList @(
            "run", "--project", $hostProject, "--no-build", "--",
            "--urls=$ServerUrl",
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=true",
            "--AuthRefresh:GateEnabled=true"
        ) -Environment $environment -RedirectStandardOutput $hostOutput -RedirectStandardError $hostError -PassThru
        Wait-ForHost $hostProcess $ServerUrl "Default"

        Write-Host "Running Default-mode smoke scenarios..."
        $clientProcess = Start-Process -FilePath $DotNetPath -ArgumentList @(
            "run", "--project", $clientProject, "--no-build", "--",
            "--server-url", $ServerUrl,
            "--user-id", "alice",
            "--role", "user",
            "--transport", "ws",
            "--auto-refresh", "off",
            "--auth-mode", "token",
            "--lifetime-seconds", "60",
            "--expiration", "on",
            "--auto-reconnect", "off"
        ) -RedirectStandardInput $clientInput -RedirectStandardOutput $clientOutput -RedirectStandardError $clientError -PassThru

        if (-not $clientProcess.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Default-mode client did not finish within $TimeoutSeconds seconds."
        }
        if ($clientProcess.ExitCode -ne 0) {
            throw "Default-mode client exited with code $($clientProcess.ExitCode)."
        }

        $output = Get-Content $clientOutput -Raw
        Write-Host "Asserting Default-mode results..."
        Assert-Match "client connected" $output "(?m)^\[conn\] connected: .+$"
        Assert-Match "negotiate advertised refresh lifetime" $output "\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=\d+; jwtLifetime=\d+s"
        Assert-Match "manual refresh succeeded" $output "\[http\] /hub/refresh => 200; tokenLifetimeSeconds=\d+; jwtLifetime=\d+s"
        Assert-Match "refresh callback ran" $output "(?m)^\[refresh\] succeeded at .+; new lifetime="
        Assert-Match "gate and user mismatch rejected" $output "\[http\] /hub/refresh => 403;.*error=permission_change_rejected" 2
        Assert-Match "GET rejected" $output "\[probe:get\] 405 MethodNotAllowed"
        Assert-Match "missing connection header rejected" $output '\[probe:missing-header\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "query-only token rejected" $output '\[probe:query-only\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "empty connection header rejected" $output '\[probe:empty-header\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "missing app auth rejected" $output "\[probe:no-auth\] 401 Unauthorized"
        Assert-Match "invalid app auth rejected" $output "\[probe:invalid-auth\] 401 Unauthorized"
        Assert-Match "unknown connection rejected" $output "\[probe:unknown\] 404 NotFound; body=.*connection_not_found"
        Assert-Match "header wins over unknown query token" $output '\[probe:header-and-query\] 200 OK; body=.*"accessToken":"[^"]+".*"tokenLifetimeSeconds":\d+'

        Write-Host "Running Default-mode TypeScript smoke scenario..."
        $tsOutput = Invoke-TypeScriptClientCase "default-typescript-smoke" $ServerUrl "ws"
        Assert-Match "TypeScript client connected" $tsOutput "(?m)^\[conn\] connected: .+$"
        Assert-Match "TypeScript refresh retained transport" $tsOutput "(?m)^\[pass\] TypeScript refresh retained connection .+"

        $connectionIds = [regex]::Matches($output, "(?m)^\[(?:conn|status)\] (?:connected:|connectionId=)(.+)$") |
            ForEach-Object { $_.Groups[1].Value.Trim() } |
            Select-Object -Unique
        if ($connectionIds.Count -ne 1) {
            throw "Assertion failed: refresh kept the connection. Found connection IDs: $($connectionIds -join ', ')."
        }
        Write-Host "  PASS  refresh kept connection $($connectionIds[0])"

        $markers = [regex]::Matches($output, "(?m)^\[marker\] server-side Context\.User marker=(.+)$") |
            ForEach-Object { $_.Groups[1].Value.Trim() }
        if ($markers.Count -lt 2 -or $markers[0] -eq $markers[1]) {
            throw "Assertion failed: claims-changing refresh did not change the marker claim."
        }
        Write-Host "  PASS  claims-changing refresh updated marker"

        Stop-ProcessTree $clientProcess
        Stop-ProcessTree $hostProcess
        $clientProcess = $null
        $hostProcess = $null
    }

    if (-not $SkipServerlessMode) {
        $functionPort = ([Uri]$FunctionUrl).Port
        $funcPath = (Get-Command "func.cmd" -ErrorAction Stop).Source
        Write-Host "Starting Serverless Function host at $FunctionUrl..."
        $environment = @{}
        if (-not $SkipServerlessMode -and -not [string]::IsNullOrWhiteSpace($ServerlessConnectionString)) {
            $environment.E2E_SERVERLESS_CONNECTION_STRING = $ServerlessConnectionString
        }
        $functionProcess = Start-Process -FilePath $funcPath -WorkingDirectory $functionOutputDirectory -ArgumentList @(
            "start", "--port", $functionPort, "--no-build"
        ) -Environment $environment -RedirectStandardOutput $functionOutput -RedirectStandardError $functionError -PassThru
        Wait-ForHost $functionProcess $FunctionUrl "Serverless"
        Wait-ForServerlessService $functionProcess $FunctionUrl

        Write-Host "Running Serverless-mode smoke scenarios..."
        $serverlessClientProcess = Start-Process -FilePath $DotNetPath -ArgumentList @(
            "run", "--project", $clientProject, "--no-build", "--",
            "--server-url", $FunctionUrl,
            "--user-id", "alice",
            "--role", "user",
            "--transport", "ws",
            "--auto-refresh", "off",
            "--auth-mode", "token",
            "--lifetime-seconds", "60",
            "--expiration", "on",
            "--scenario", "standard",
            "--auto-reconnect", "off"
        ) -RedirectStandardInput $serverlessClientInput -RedirectStandardOutput $serverlessClientOutput -RedirectStandardError $serverlessClientError -PassThru

        if (-not $serverlessClientProcess.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Serverless client did not finish within $TimeoutSeconds seconds."
        }
        if ($serverlessClientProcess.ExitCode -ne 0) {
            throw "Serverless client exited with code $($serverlessClientProcess.ExitCode)."
        }

        $serverlessOutput = Get-Content $serverlessClientOutput -Raw
        Write-Host "Asserting Serverless-mode results..."
        Assert-Match "Serverless client connected" $serverlessOutput "(?m)^\[conn\] connected: .+$"
        Assert-Match "Serverless negotiate advertised refresh lifetime" $serverlessOutput "\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=\d+; jwtLifetime=\d+s"
        Assert-Match "Serverless manual refresh succeeded" $serverlessOutput "\[http\] /hub/refresh => 200; tokenLifetimeSeconds=\d+; jwtLifetime=\d+s"
        Assert-Match "Serverless claims read succeeded" $serverlessOutput '\[claims\] 200 OK; body=.*"claims"' 2
        Assert-Match "Serverless gate rejected blocked role" $serverlessOutput "\[http\] /hub/refresh => 403;.*permission_change_rejected"
        Assert-Match "Serverless same-user enforcement rejected mismatch" $serverlessOutput "\[http\] /hub/refresh => 403;.*refresh_failed"
        Assert-Match "Serverless missing connection header rejected" $serverlessOutput '\[probe:missing-header\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "Serverless query-only token rejected" $serverlessOutput '\[probe:query-only\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "Serverless empty connection header rejected" $serverlessOutput '\[probe:empty-header\] 400 BadRequest; body=.*"error":"missing_connection_token"'
        Assert-Match "Serverless missing app auth rejected" $serverlessOutput "\[probe:no-auth\] 401 Unauthorized"
        Assert-Match "Serverless invalid app auth rejected" $serverlessOutput "\[probe:invalid-auth\] 401 Unauthorized"
        Assert-Match "Serverless header wins over unknown query token" $serverlessOutput '\[probe:header-and-query\] 200 OK; body=.*"accessToken":"[^"]+".*"tokenLifetimeSeconds":\d+'

        $serverlessConnectionIds = [regex]::Matches($serverlessOutput, "(?m)^\[conn\] connected: (.+)$") |
            ForEach-Object { $_.Groups[1].Value.Trim() } |
            Select-Object -Unique
        if ($serverlessConnectionIds.Count -ne 1) {
            throw "Assertion failed: Serverless refresh kept the connection. Found connection IDs: $($serverlessConnectionIds -join ', ')."
        }
        Write-Host "  PASS  Serverless refresh kept connection $($serverlessConnectionIds[0])"

        $serverlessMarkers = [regex]::Matches($serverlessOutput, '\"type\":\"marker\",\"value\":\"([^\"]+)\"') |
            ForEach-Object { $_.Groups[1].Value }
        if ($serverlessMarkers.Count -lt 2 -or $serverlessMarkers[0] -eq $serverlessMarkers[1]) {
            throw "Assertion failed: Serverless claims-changing refresh did not change the marker claim."
        }
        Write-Host "  PASS  Serverless claims-changing refresh updated marker"
    }

    Write-Host "E2E smoke test passed. Logs: $artifactsDirectory"
}
catch {
    Write-Error $_
    Write-Host "Default host log:     $hostOutput"
    Write-Host "Default client log:   $clientOutput"
    Write-Host "Serverless host log:  $functionOutput"
    Write-Host "Serverless client log: $serverlessClientOutput"
    exit 1
}
finally {
    Stop-ProcessTree $serverlessClientProcess
    Stop-ProcessTree $functionProcess
    Stop-ProcessTree $clientProcess
    Stop-ProcessTree $hostProcess
}