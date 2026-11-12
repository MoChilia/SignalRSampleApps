[CmdletBinding()]
param(
    [string] $ServerUrl = "http://localhost:5121",
    [string] $FunctionUrl = "http://localhost:7071",
    [string] $OldSdkUrl = "http://localhost:5222",
    [string] $CrossServerAppAUrl = "http://localhost:5122",
    [string] $CrossServerAppBUrl = "http://localhost:5123",
    [string] $DotNetPath = "dotnet",
    [string] $NodePath = "node",
    [string] $DefaultConnectionString = $env:DEFAULT_SIGNALR_CONNECTION_STRING,
    [string] $ServerlessConnectionString = $env:SERVERLESS_SIGNALR_CONNECTION_STRING,
    [Alias("UpstreamConnectionString")]
    [string] $RuntimeConnectionString = "Endpoint=http://localhost;Port=8888;AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;",
    [string] $SecondaryRuntimeConnectionString,
    [int] $TimeoutSeconds = 120,
    [switch] $IncludeAutoRefresh,
    [switch] $IncludeCompatibility,
    [switch] $IncludeLongRunning,
    [Alias("IncludeUpstream")]
    [switch] $IncludeRuntimeUpstream,
    [Alias("UpstreamOnly")]
    [switch] $RuntimeUpstreamOnly,
    [switch] $SkipBuild,
    [switch] $SkipDefaultMode,
    [switch] $SkipServerlessMode
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
$defaultProject = Join-Path $scriptRoot "refreshAuthApp\AzureSignalRRefreshAuthSample.csproj"
$functionProject = Join-Path $scriptRoot "refreshAuthFunctionApp\refreshAuthFunctionApp.csproj"
$functionOutputDirectory = Join-Path (Split-Path $functionProject -Parent) "bin\Debug\net8.0"
$clientProject = Join-Path $scriptRoot "refreshAuthClient\refreshAuthClient.csproj"
$oldSdkProject = Join-Path $scriptRoot "compatibility\oldSdkHost\OldSdkHost.csproj"
$proxyScript = Join-Path $scriptRoot "refreshAuthProxy\proxy.mjs"
$typeScriptClientDirectory = Join-Path $scriptRoot "refreshAuthTsClient"
$aspNetCoreRoot = Join-Path $env:USERPROFILE "aspnetcore"
$signalRTypeScriptClientDirectory = Join-Path $aspNetCoreRoot "src\SignalR\clients\ts\signalr"
$artifactsRoot = Join-Path $scriptRoot ".e2e-artifacts\matrix"
$runId = "{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), $PID
$artifactsDirectory = Join-Path $artifactsRoot $runId
$restoreSources = @(
    "https://packagefeedproxy.microsoft.io/nuget/v3/index.json"
    (Join-Path $aspNetCoreRoot "artifacts\packages\Debug\Shipping")
) -join ";"
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-DotNetBuild {
    param(
        [string] $Project,
        [string[]] $AdditionalArguments = @()
    )

    & $DotNetPath build $Project --nologo "-p:RestoreSources=$restoreSources" /p:NuGetAudit=false @AdditionalArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $Project (exit code $LASTEXITCODE)."
    }
}

function Stop-ProcessTree {
    param([System.Diagnostics.Process] $Process)

    if ($null -ne $Process -and -not $Process.HasExited) {
        & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
        if (-not $Process.WaitForExit(5000)) {
            throw "Process tree rooted at PID $($Process.Id) did not exit within 5 seconds."
        }
    }
}

function Assert-HostPortAvailable {
    param([string] $BaseUrl)

    $port = ([Uri]$BaseUrl).Port
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $listener) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
    $ownerDescription = if ($null -eq $owner) {
        "PID $($listener.OwningProcess)"
    }
    else {
        "PID $($owner.ProcessId) ($($owner.Name)): $($owner.CommandLine)"
    }
    throw "Cannot start the sample host at $BaseUrl because port $port is already in use by $ownerDescription. Stop that process or pass a different -ServerUrl."
}

function Assert-DefaultConfiguration {
    if (-not [string]::IsNullOrWhiteSpace($DefaultConnectionString)) {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($env:Azure__SignalR__ConnectionString)) {
        return
    }

    $secretKeys = & $DotNetPath user-secrets list --project $defaultProject 2>$null |
        ForEach-Object { ($_ -split " = ", 2)[0].Trim() }
    if ($secretKeys -contains "Azure:SignalR:ConnectionString") {
        return
    }

    throw @"
Default mode requires an Azure SignalR connection string. Configure one before running the matrix:

  dotnet user-secrets set "Azure:SignalR:ConnectionString" "<connection-string>" --project "$defaultProject"

Alternatively, pass -DefaultConnectionString or set DEFAULT_SIGNALR_CONNECTION_STRING in the current PowerShell session.
"@
}

function Get-DefaultConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($DefaultConnectionString)) {
        return $DefaultConnectionString
    }
    if (-not [string]::IsNullOrWhiteSpace($env:Azure__SignalR__ConnectionString)) {
        return $env:Azure__SignalR__ConnectionString
    }

    $prefix = "Azure:SignalR:ConnectionString = "
    $entry = & $DotNetPath user-secrets list --project $defaultProject 2>$null |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if ($null -eq $entry) {
        return $null
    }
    return $entry.Substring($prefix.Length)
}

function Wait-ForSampleHost {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $BaseUrl,
        [string] $Mode,
        [string] $ErrorLog,
        [Nullable[bool]] $ExpectedAuthenticationRefresh,
        [string] $ExpectedServiceTransportType
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            $detail = if (Test-Path $ErrorLog) { (Get-Content $ErrorLog -Tail 20) -join [Environment]::NewLine } else { "No stderr log was written." }
            throw "$Mode host exited before becoming ready (exit code $($Process.ExitCode)).`n$detail"
        }
        try {
            $config = Invoke-RestMethod -Uri "$BaseUrl/sample-config" -TimeoutSec 2
            if (($Mode -eq "Default" -and
                    $null -ne $config.enableAuthenticationRefresh -and
                    ($null -eq $ExpectedAuthenticationRefresh -or
                        $config.enableAuthenticationRefresh -eq [bool]$ExpectedAuthenticationRefresh)) -or
                ($Mode -eq "Serverless" -and
                    $config.mode -eq "Serverless" -and
                    ([string]::IsNullOrWhiteSpace($ExpectedServiceTransportType) -or
                        $config.serviceTransportType -eq $ExpectedServiceTransportType))) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 300
        }
    }
    throw "$Mode host did not become ready at $BaseUrl within $TimeoutSeconds seconds."
}

function Wait-ForServerlessService {
    param([System.Diagnostics.Process] $Process)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $body = @{
        userId = "matrix-readiness"
        role = "user"
        lifetimeSeconds = 60
        includeExpiration = $true
        scenario = "standard"
    } | ConvertTo-Json

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Serverless host exited while waiting for its service connection."
        }
        try {
            $token = Invoke-RestMethod -Method Post -Uri "$FunctionUrl/token" -ContentType "application/json" -Body $body -TimeoutSec 5
            $headers = @{ Authorization = "Bearer $($token.accessToken)" }
            $result = Invoke-RestMethod -Method Post -Uri "$FunctionUrl/hub/negotiate" -Headers $headers -TimeoutSec 5
            if (-not [string]::IsNullOrWhiteSpace($result.accessToken)) {
                $clientNegotiateUriBuilder = [UriBuilder]$result.url
                $clientNegotiateUriBuilder.Path = "$($clientNegotiateUriBuilder.Path.TrimEnd('/'))/negotiate"
                $redirectQuery = $clientNegotiateUriBuilder.Query.TrimStart('?')
                $clientNegotiateUriBuilder.Query = if ([string]::IsNullOrEmpty($redirectQuery)) {
                    "negotiateVersion=1"
                }
                else {
                    "$redirectQuery&negotiateVersion=1"
                }
                $clientNegotiateUri = $clientNegotiateUriBuilder.Uri.AbsoluteUri
                $clientHeaders = @{ Authorization = "Bearer $($result.accessToken)" }
                Invoke-RestMethod -Method Post -Uri $clientNegotiateUri -Headers $clientHeaders -TimeoutSec 5 | Out-Null
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Serverless service connection or negotiated client endpoint did not become ready within $TimeoutSeconds seconds. For a local runtime, configure ServiceType=SignalR, ServiceMode=Serverless, and include ClientEndpoint=http://localhost:<port> in the connection string."
}

function Wait-ForDefaultService {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $BaseUrl
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $body = @{
        userId = "matrix-readiness"
        role = "user"
        lifetimeSeconds = 60
        includeExpiration = $true
        scenario = "standard"
    } | ConvertTo-Json

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Default host exited while waiting for its Azure SignalR service connection."
        }
        try {
            $token = Invoke-RestMethod -Method Post -Uri "$BaseUrl/token" -ContentType "application/json" -Body $body -TimeoutSec 5
            $headers = @{ Authorization = "Bearer $($token.accessToken)" }
            $result = Invoke-RestMethod -Method Post -Uri "$BaseUrl/hub/negotiate?negotiateVersion=1" -Headers $headers -TimeoutSec 5
            if (-not [string]::IsNullOrWhiteSpace($result.accessToken)) {
                $clientNegotiateUriBuilder = [UriBuilder]$result.url
                $clientNegotiateUriBuilder.Path = "$($clientNegotiateUriBuilder.Path.TrimEnd('/'))/negotiate"
                $redirectQuery = $clientNegotiateUriBuilder.Query.TrimStart('?')
                $clientNegotiateUriBuilder.Query = if ([string]::IsNullOrEmpty($redirectQuery)) {
                    "negotiateVersion=1"
                }
                else {
                    "$redirectQuery&negotiateVersion=1"
                }
                $clientHeaders = @{ Authorization = "Bearer $($result.accessToken)" }
                Invoke-RestMethod -Method Post -Uri $clientNegotiateUriBuilder.Uri.AbsoluteUri -Headers $clientHeaders -TimeoutSec 5 | Out-Null
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Default Azure SignalR service connection or negotiated client endpoint did not become ready within $TimeoutSeconds seconds. If the connection string uses a local ClientEndpoint, start that runtime before running the matrix."
}

function Start-DefaultHost {
    param(
        [string] $Name,
        [string[]] $Options,
        [string] $BaseUrl = $ServerUrl
    )

    Assert-HostPortAvailable $BaseUrl
    $stdout = Join-Path $artifactsDirectory "$Name.host.stdout.log"
    $stderr = Join-Path $artifactsDirectory "$Name.host.stderr.log"
    $arguments = @("run", "--project", $defaultProject, "--no-build", "--", "--urls=$BaseUrl") + $Options
    $environment = @{ ASPNETCORE_ENVIRONMENT = "Development" }
    if (-not [string]::IsNullOrWhiteSpace($DefaultConnectionString)) {
        $environment.Azure__SignalR__ConnectionString = $DefaultConnectionString
    }
    $process = Start-Process -FilePath $DotNetPath -ArgumentList $arguments `
        -Environment $environment `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $script:hostProcess = $process
    $expectedAuthenticationRefresh = if ($Options -contains "--AuthRefresh:Enabled=false") { $false } else { $true }
    Wait-ForSampleHost $process $BaseUrl "Default" $stderr $expectedAuthenticationRefresh
    Wait-ForDefaultService $process $BaseUrl
    return $process
}

function Start-RefreshProxy {
    Assert-HostPortAvailable $ServerUrl
    $stdout = Join-Path $artifactsDirectory "default-cross-server.proxy.stdout.log"
    $stderr = Join-Path $artifactsDirectory "default-cross-server.proxy.stderr.log"
    $process = Start-Process -FilePath $NodePath -ArgumentList @(
        $proxyScript,
        "--listen", $ServerUrl,
        "--app-a", $CrossServerAppAUrl,
        "--app-b", $CrossServerAppBUrl
    ) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $detail = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { "No stderr log was written." }
            throw "Cross-server proxy exited before becoming ready.`n$detail"
        }
        try {
            Invoke-RestMethod -Uri "$ServerUrl/sample-config" -TimeoutSec 2 | Out-Null
            return $process
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    throw "Cross-server proxy did not become ready at $ServerUrl within $TimeoutSeconds seconds."
}

function Start-OldSdkHost {
    Assert-HostPortAvailable $OldSdkUrl
    $stdout = Join-Path $artifactsDirectory "old-sdk.host.stdout.log"
    $stderr = Join-Path $artifactsDirectory "old-sdk.host.stderr.log"
    $environment = @{ ASPNETCORE_ENVIRONMENT = "Development" }
    $connectionString = Get-DefaultConnectionString
    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        $environment.Azure__SignalR__ConnectionString = $connectionString
    }
    $process = Start-Process -FilePath $DotNetPath -ArgumentList @(
        "run", "--project", $oldSdkProject, "--no-build", "--", "--urls=$OldSdkUrl"
    ) -Environment $environment -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $script:hostProcess = $process
    Wait-ForSampleHost $process $OldSdkUrl "Default" $stderr $true
    Wait-ForDefaultService $process $OldSdkUrl
    return $process
}

function Start-ServerlessHost {
    param(
        [string] $FirstRuntimeConnectionString = $RuntimeConnectionString,
        [string] $SecondRuntimeConnectionString = $SecondaryRuntimeConnectionString,
        [ValidateSet("Transient", "Persistent")]
        [string] $ServiceTransportType = "Persistent"
    )

    $funcPath = (Get-Command "func.cmd" -ErrorAction Stop).Source
    $port = ([Uri]$FunctionUrl).Port
    Assert-HostPortAvailable $FunctionUrl
    $logPrefix = if ($ServiceTransportType -eq "Transient") { "serverless-transient" } else { "serverless" }
    $stdout = Join-Path $artifactsDirectory "$logPrefix.host.stdout.log"
    $stderr = Join-Path $artifactsDirectory "$logPrefix.host.stderr.log"
    $environment = @{
        Azure__SignalR__ServiceTransportType = $ServiceTransportType
        AzureSignalRServiceTransportType = $ServiceTransportType
    }
    $connectionString = if ($IncludeRuntimeUpstream) { $FirstRuntimeConnectionString } else { $ServerlessConnectionString }
    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        $environment.E2E_SERVERLESS_CONNECTION_STRING = $connectionString
    }
    if ($IncludeRuntimeUpstream) {
        if (-not [string]::IsNullOrWhiteSpace($SecondRuntimeConnectionString)) {
            # Both the Management SDK and Functions SignalR bindings consume Azure:SignalR:Endpoints.
            # Clear the single-endpoint overrides so neither path adds a duplicate endpoint.
            $environment.E2E_SERVERLESS_CONNECTION_STRING = ""
            $environment.Azure__SignalR__ConnectionString = ""
            $environment.Azure__SignalR__Endpoints__Endpoint1 = $FirstRuntimeConnectionString
            $environment.Azure__SignalR__Endpoints__Endpoint2 = $SecondRuntimeConnectionString
        }
        else {
            $environment.RuntimeAzureSignalRConnectionString = $FirstRuntimeConnectionString
        }
    }
    $process = Start-Process -FilePath $funcPath -WorkingDirectory $functionOutputDirectory `
        -ArgumentList @("start", "--port", $port, "--no-build") `
        -Environment $environment `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $script:hostProcess = $process
    Wait-ForSampleHost $process $FunctionUrl "Serverless" $stderr $null $ServiceTransportType
    Wait-ForServerlessService $process
    if ($IncludeRuntimeUpstream) {
        $indexedFunctions = Get-Content $stdout -Raw
        if ($indexedFunctions -match "(?i)(failed indexing|function is in error)") {
            throw "One or more upstream functions failed indexing. See $stdout."
        }
        foreach ($functionName in @("SendToAll", "WhoAmIMarker", "WhoAmIState")) {
            if ($indexedFunctions -notmatch "(?m)^\s+$([regex]::Escape($functionName)):\s") {
                throw "The Function host did not index the upstream function '$functionName'. Rebuild without -SkipBuild and ensure EnableUpstreamFunctions=true. See $stdout."
            }
        }
    }
    return $process
}

function Invoke-ClientCase {
    param(
        [string] $Name,
        [string] $BaseUrl,
        [string] $Transport = "ws",
        [string] $AuthMode = "token",
        [string] $Scenario = "standard",
        [string] $Expiration = "on",
        [string[]] $Commands = @("/refresh"),
        [int] $LifetimeSeconds = 60,
        [int] $NegotiateVersion = 1,
        [string] $AutoRefresh = "off",
        [string] $AutoReconnect = "off",
        [int] $RefreshBeforeSeconds = 20,
        [string] $IgnoreRefreshedAccessToken = "off",
        [Nullable[int]] $ReconnectDelaySeconds,
        [string] $EndpointName
    )

    Write-Host "CASE  $Name"
    $stdin = Join-Path $artifactsDirectory "$Name.stdin.txt"
    $stdout = Join-Path $artifactsDirectory "$Name.client.stdout.log"
    $stderr = Join-Path $artifactsDirectory "$Name.client.stderr.log"
    Set-Content -Path $stdin -Value $Commands -Encoding utf8

    $arguments = @(
        "run", "--project", $clientProject, "--no-build", "--",
        "--server-url", $BaseUrl,
        "--user-id", "alice",
        "--role", "user",
        "--transport", $Transport,
        "--auto-refresh", $AutoRefresh,
        "--auth-mode", $AuthMode,
        "--lifetime-seconds", $LifetimeSeconds,
        "--expiration", $Expiration,
        "--scenario", $Scenario,
        "--auto-reconnect", $AutoReconnect,
        "--negotiate-version", $NegotiateVersion,
        "--refresh-before-seconds", $RefreshBeforeSeconds,
        "--ignore-refreshed-access-token", $IgnoreRefreshedAccessToken
    )
    if ($null -ne $ReconnectDelaySeconds) {
        $arguments += @("--reconnect-delay-seconds", $ReconnectDelaySeconds)
    }
    if (-not [string]::IsNullOrWhiteSpace($EndpointName)) {
        $arguments += @("--endpoint-name", $EndpointName)
    }
    $process = Start-Process -FilePath $DotNetPath -ArgumentList $arguments `
        -RedirectStandardInput $stdin -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "client timed out after $TimeoutSeconds seconds"
        }
        return Get-Content $stdout -Raw
    }
    finally {
        Stop-ProcessTree $process
    }
}

function Invoke-TypeScriptClientCase {
    param(
        [string] $Name,
        [string] $BaseUrl,
        [string] $Transport = "ws",
        [string] $AutoRefresh = "off",
        [int] $ExpectedAutoRefreshes = 0,
        [int] $LifetimeSeconds = 60,
        [int] $RefreshBeforeMilliseconds = 20000
    )

    Write-Host "CASE  $Name"
    $stdout = Join-Path $artifactsDirectory "$Name.client.stdout.log"
    $stderr = Join-Path $artifactsDirectory "$Name.client.stderr.log"
    $process = Start-Process -FilePath $NodePath -WorkingDirectory $typeScriptClientDirectory -ArgumentList @(
        "dist/index.js",
        "--server-url", $BaseUrl,
        "--user-id", "alice",
        "--role", "user",
        "--transport", $Transport,
        "--auto-refresh", $AutoRefresh,
        "--expected-auto-refreshes", $ExpectedAutoRefreshes,
        "--auto-refresh-timeout-ms", ($TimeoutSeconds * 1000),
        "--lifetime-seconds", $LifetimeSeconds,
        "--refresh-before-ms", $RefreshBeforeMilliseconds
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

function Assert-CaseMatch {
    param(
        [string] $Case,
        [string] $Text,
        [string] $Description,
        [string] $Pattern,
        [int] $MinimumCount = 1
    )

    $count = [regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count
    if ($count -lt $MinimumCount) {
        $failures.Add("$Case`: $Description (expected $MinimumCount match(es), found $count)")
        Write-Host "  FAIL  $Description" -ForegroundColor Red
        return $false
    }
    Write-Host "  PASS  $Description"
    return $true
}

function Assert-CaseNoMatch {
    param(
        [string] $Case,
        [string] $Text,
        [string] $Description,
        [string] $Pattern
    )

    if ([regex]::IsMatch($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        $failures.Add("$Case`: $Description")
        Write-Host "  FAIL  $Description" -ForegroundColor Red
        return $false
    }
    Write-Host "  PASS  $Description"
    return $true
}

function Assert-UpstreamRefreshCase {
    param(
        [string] $Case,
        [string] $Text,
        [string] $FunctionLog,
        [string] $BeforeMessage,
        [string] $AfterMessage
    )

    Test-ConnectedRefreshCase $Case $Text

    $markers = @([regex]::Matches($Text, '(?m)^\[marker\] server-side Context\.User marker=(.+)$') |
        ForEach-Object { $_.Groups[1].Value.Trim() })
    if ($markers.Count -ge 2 -and $markers[0] -ne $markers[1]) {
        Write-Host "  PASS  upstream invocation observed refreshed marker"
    }
    else {
        $failures.Add("$Case`: upstream invocation did not observe a changed marker")
        Write-Host "  FAIL  upstream invocation observed refreshed marker" -ForegroundColor Red
        return
    }

    $beforeMarker = [regex]::Escape($markers[0])
    $afterMarker = [regex]::Escape($markers[1])
    Assert-CaseMatch $Case $Text "pre-refresh upstream broadcast used original claims" `
        "(?m)^\[recv\] .*: $([regex]::Escape($BeforeMessage)) \[marker=$beforeMarker\]\r?$" | Out-Null
    Assert-CaseMatch $Case $Text "post-refresh upstream broadcast used refreshed claims" `
        "(?m)^\[recv\] .*: $([regex]::Escape($AfterMessage)) \[marker=$afterMarker\]\r?$" | Out-Null

    $initialConnectionId = [regex]::Match($Text, '(?m)^\[conn\] connected: (.+)$').Groups[1].Value.Trim()
    Assert-CaseMatch $Case $Text "refresh retained the client connection" `
        "(?m)^\[status\] connectionId=$([regex]::Escape($initialConnectionId))\r?$" | Out-Null
    Assert-CaseNoMatch $Case $Text "upstream refresh did not reconnect" '^\[conn\] RECONNECT(?:ING|ED)' | Out-Null

    Assert-CaseMatch $Case $FunctionLog "Function received pre-refresh upstream claims" `
        "Upstream (?:WhoAmIMarker|SendToAll).*marker=$beforeMarker" | Out-Null
    Assert-CaseMatch $Case $FunctionLog "Function received post-refresh upstream claims" `
        "Upstream (?:WhoAmIMarker|SendToAll).*marker=$afterMarker" | Out-Null
    Assert-CaseMatch $Case $FunctionLog "raw upstream claims header contained original marker" `
        "Raw X-ASRS-User-Claims header: \[[^\]]*marker: $beforeMarker(?:,|\])" | Out-Null
    Assert-CaseMatch $Case $FunctionLog "raw upstream claims header contained refreshed marker" `
        "Raw X-ASRS-User-Claims header: \[[^\]]*marker: $afterMarker(?:,|\])" | Out-Null
}

function Assert-NegotiatedEndpoint {
    param(
        [string] $Case,
        [string] $Text,
        [string] $ConnectionString
    )

    $properties = @{}
    foreach ($part in $ConnectionString.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        $pair = $part.Split('=', 2)
        if ($pair.Count -eq 2) {
            $properties[$pair[0].Trim()] = $pair[1].Trim()
        }
    }

    $endpoint = if ($properties.ContainsKey("ClientEndpoint")) {
        $properties["ClientEndpoint"]
    }
    else {
        $builder = [UriBuilder]$properties["Endpoint"]
        if ($properties.ContainsKey("Port")) {
            $builder.Port = [int]$properties["Port"]
        }
        $builder.Uri.GetLeftPart([UriPartial]::Authority)
    }

    Assert-CaseMatch $Case $Text "client negotiated to expected owning endpoint $endpoint" `
        "(?m)^\[http\] /hub/negotiate => 200;.*url=$([regex]::Escape($endpoint))/client/" | Out-Null
}

function Get-ClaimSnapshots {
    param([string] $Text)

    $snapshots = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($match in [regex]::Matches($Text, '(?m)^\[claims\] (?:200 OK|Default hub principal); body=(\{.*\})\r?$')) {
        $body = $match.Groups[1].Value | ConvertFrom-Json
        $claims = @{}
        foreach ($claim in $body.claims) {
            $claims[$claim.type] = $claim.value
        }
        if ($null -ne $body.authenticationRefresh) {
            $claims["__defaultRefreshCount"] = [int]$body.authenticationRefresh.count
            $claims["__defaultPreviousRefreshExpiration"] = $body.authenticationRefresh.previousExpirationUnixSeconds
            $claims["__defaultRefreshExpiration"] = $body.authenticationRefresh.newExpirationUnixSeconds
        }
        $snapshots.Add($claims)
    }
    return $snapshots
}

function Assert-ClaimChange {
    param(
        [string] $Case,
        [string] $Text,
        [ValidateSet("Changed", "Preserved", "Absent")] [string] $Marker,
        [ValidateSet("Changed", "Preserved", "Absent")] [string] $Deadline
    )

    $snapshots = @(Get-ClaimSnapshots $Text)
    if ($snapshots.Count -lt 2) {
        $failures.Add("$Case`: expected two successful claims snapshots")
        Write-Host "  FAIL  claims snapshots" -ForegroundColor Red
        return
    }

    foreach ($expectation in @(@{ Name = "marker"; Mode = $Marker })) {
        $before = $snapshots[0][$expectation.Name]
        $after = $snapshots[-1][$expectation.Name]
        $passed = switch ($expectation.Mode) {
            "Changed" { -not [string]::IsNullOrEmpty($before) -and -not [string]::IsNullOrEmpty($after) -and $before -ne $after }
            "Preserved" { -not [string]::IsNullOrEmpty($before) -and $before -eq $after }
            "Absent" { [string]::IsNullOrEmpty($before) -and [string]::IsNullOrEmpty($after) }
        }
        if ($passed) {
            Write-Host "  PASS  $($expectation.Name) $($expectation.Mode.ToLowerInvariant())"
        }
        else {
            $failures.Add("$Case`: expected $($expectation.Name) to be $($expectation.Mode.ToLowerInvariant())")
            Write-Host "  FAIL  $($expectation.Name) $($expectation.Mode.ToLowerInvariant())" -ForegroundColor Red
        }
    }

    $beforeDeadline = $snapshots[0]["asrs.s.aeo"]
    $afterDeadline = $snapshots[-1]["asrs.s.aeo"]
    if ($snapshots[0].ContainsKey("__defaultRefreshCount")) {
        $beforeCount = $snapshots[0]["__defaultRefreshCount"]
        $afterCount = $snapshots[-1]["__defaultRefreshCount"]
        $previousExpiration = $snapshots[-1]["__defaultPreviousRefreshExpiration"]
        $afterExpiration = $snapshots[-1]["__defaultRefreshExpiration"]
        $systemClaimsHidden = [string]::IsNullOrEmpty($beforeDeadline) -and [string]::IsNullOrEmpty($afterDeadline)
        $deadlinePassed = switch ($Deadline) {
            "Changed" { $afterCount -ge $beforeCount + 2 -and -not [string]::IsNullOrEmpty($previousExpiration) -and -not [string]::IsNullOrEmpty($afterExpiration) -and $afterExpiration -gt $previousExpiration }
            "Preserved" { $afterCount -ge $beforeCount + 2 -and -not [string]::IsNullOrEmpty($afterExpiration) -and $afterExpiration -eq $previousExpiration }
            "Absent" { $afterCount -ge $beforeCount + 2 -and [string]::IsNullOrEmpty($previousExpiration) -and [string]::IsNullOrEmpty($afterExpiration) }
        }
        if ($systemClaimsHidden) {
            Write-Host "  PASS  asrs.s.aeo hidden from Context.User"
        }
        else {
            $failures.Add("$Case`: asrs.s.aeo leaked into Context.User")
            Write-Host "  FAIL  asrs.s.aeo hidden from Context.User" -ForegroundColor Red
        }
        if ($deadlinePassed) {
            Write-Host "  PASS  refresh NewExpiration $($Deadline.ToLowerInvariant())"
        }
        else {
            $failures.Add("$Case`: expected refresh NewExpiration to be $($Deadline.ToLowerInvariant())")
            Write-Host "  FAIL  refresh NewExpiration $($Deadline.ToLowerInvariant())" -ForegroundColor Red
        }
        return
    }

    $deadlinePassed = switch ($Deadline) {
        "Changed" { -not [string]::IsNullOrEmpty($beforeDeadline) -and -not [string]::IsNullOrEmpty($afterDeadline) -and $beforeDeadline -ne $afterDeadline }
        "Preserved" { -not [string]::IsNullOrEmpty($beforeDeadline) -and $beforeDeadline -eq $afterDeadline }
        "Absent" { [string]::IsNullOrEmpty($beforeDeadline) -and [string]::IsNullOrEmpty($afterDeadline) }
    }
    if ($deadlinePassed) {
        Write-Host "  PASS  asrs.s.aeo $($Deadline.ToLowerInvariant())"
    }
    else {
        $failures.Add("$Case`: expected asrs.s.aeo to be $($Deadline.ToLowerInvariant())")
        Write-Host "  FAIL  asrs.s.aeo $($Deadline.ToLowerInvariant())" -ForegroundColor Red
    }
}

function Assert-DefaultClaimsPreserved {
    param(
        [string] $Case,
        [string] $Text
    )

    $snapshots = @(Get-ClaimSnapshots $Text)
    if ($snapshots.Count -lt 2) {
        $failures.Add("$Case`: expected two successful claims snapshots")
        Write-Host "  FAIL  claims snapshots" -ForegroundColor Red
        return
    }

    $beforeMarker = $snapshots[0]["marker"]
    $afterMarker = $snapshots[-1]["marker"]
    if (-not [string]::IsNullOrEmpty($beforeMarker) -and $beforeMarker -eq $afterMarker) {
        Write-Host "  PASS  marker preserved"
    }
    else {
        $failures.Add("$Case`: expected marker to be preserved")
        Write-Host "  FAIL  marker preserved" -ForegroundColor Red
    }

    if ([string]::IsNullOrEmpty($snapshots[0]["asrs.s.aeo"]) -and [string]::IsNullOrEmpty($snapshots[-1]["asrs.s.aeo"])) {
        Write-Host "  PASS  asrs.s.aeo hidden from Context.User"
    }
    else {
        $failures.Add("$Case`: asrs.s.aeo leaked into Context.User")
        Write-Host "  FAIL  asrs.s.aeo hidden from Context.User" -ForegroundColor Red
    }
}

function Assert-ServiceTokenExpirationAdvanced {
    param(
        [string] $Case,
        [string] $Text
    )

    $tokenExpirationMatches = @([regex]::Matches($Text, '(?m)^\[http\] /hub/(?:negotiate|refresh) => 200;.*?; exp=([^;]+);'))
    if ($tokenExpirationMatches.Count -lt 2) {
        $failures.Add("$Case`: expected successful negotiate and refresh token expirations")
        Write-Host "  FAIL  service token expiration snapshots" -ForegroundColor Red
        return
    }

    $before = [DateTimeOffset]::Parse($tokenExpirationMatches[0].Groups[1].Value)
    $after = [DateTimeOffset]::Parse($tokenExpirationMatches[-1].Groups[1].Value)
    if ($after -gt $before) {
        Write-Host "  PASS  service token expiration advanced"
    }
    else {
        $failures.Add("$Case`: expected service token expiration to advance")
        Write-Host "  FAIL  service token expiration advanced" -ForegroundColor Red
    }
}

function Assert-RejectedRefreshPreservedState {
    param(
        [string] $Case,
        [string] $Text
    )

    $snapshots = @(Get-ClaimSnapshots $Text)
    if ($snapshots.Count -lt 3) {
        $failures.Add("$Case`: expected snapshots before and after both rejected refreshes")
        Write-Host "  FAIL  rejected refresh snapshots" -ForegroundColor Red
        return
    }

    $initial = $snapshots[0]
    $preserved = $true
    foreach ($snapshot in $snapshots[1..($snapshots.Count - 1)]) {
        $preserved = $preserved `
            -and $snapshot["marker"] -eq $initial["marker"] `
            -and $snapshot["role"] -eq $initial["role"] `
            -and $snapshot["__defaultRefreshCount"] -eq $initial["__defaultRefreshCount"] `
            -and $snapshot["__defaultRefreshExpiration"] -eq $initial["__defaultRefreshExpiration"]
    }
    if ($preserved) {
        Write-Host "  PASS  rejected refreshes preserved principal and refresh metadata"
    }
    else {
        $failures.Add("$Case`: rejected refresh mutated principal or refresh metadata")
        Write-Host "  FAIL  rejected refreshes preserved principal and refresh metadata" -ForegroundColor Red
    }
}

function Assert-RefreshCount {
    param(
        [string] $Case,
        [string] $Text,
        [int] $ExpectedDelta
    )

    $snapshots = @(Get-ClaimSnapshots $Text)
    $actualDelta = if ($snapshots.Count -ge 2) {
        $snapshots[-1]["__defaultRefreshCount"] - $snapshots[0]["__defaultRefreshCount"]
    }
    else { -1 }
    if ($actualDelta -eq $ExpectedDelta) {
        Write-Host "  PASS  $ExpectedDelta server refresh notification(s)"
    }
    else {
        $failures.Add("$Case`: expected $ExpectedDelta server refresh notifications, found $actualDelta")
        Write-Host "  FAIL  $ExpectedDelta server refresh notification(s)" -ForegroundColor Red
    }
}

function Assert-MixedLifetimeDeadline {
    param(
        [string] $Case,
        [string] $Text
    )

    $expirations = @([regex]::Matches($Text, '(?m)^\[token:parallel-lifetime-\d+\].*expiresAt=(\d+)') |
        ForEach-Object { [long]$_.Groups[1].Value })
    $snapshots = @(Get-ClaimSnapshots $Text)
    $actual = if ($snapshots.Count -ge 2) { [long]$snapshots[-1]["__defaultRefreshExpiration"] } else { 0 }
    $expected = if ($expirations.Count -gt 0) { [long]($expirations | Measure-Object -Maximum).Maximum } else { 0 }
    if ($expirations.Count -ge 2 -and $actual -eq $expected) {
        Write-Host "  PASS  final deadline equals maximum submitted expiration"
    }
    else {
        $failures.Add("$Case`: final deadline $actual did not equal maximum submitted expiration $expected")
        Write-Host "  FAIL  final deadline equals maximum submitted expiration" -ForegroundColor Red
    }
}

function Assert-TokenLifetimeAtMost {
    param(
        [string] $Case,
        [string] $Text,
        [int] $MaximumSeconds
    )

    $lifetimes = @([regex]::Matches($Text, '(?m)^\[http\] /hub/(?:negotiate|refresh) => 200; tokenLifetimeSeconds=(\d+);') |
        ForEach-Object { [int]$_.Groups[1].Value })
    if ($lifetimes.Count -gt 0 -and @($lifetimes | Where-Object { $_ -le 0 -or $_ -gt $MaximumSeconds }).Count -eq 0) {
        Write-Host "  PASS  service token lifetimes capped at ${MaximumSeconds}s"
    }
    else {
        $failures.Add("$Case`: expected positive service token lifetimes at most ${MaximumSeconds}s")
        Write-Host "  FAIL  service token lifetimes capped at ${MaximumSeconds}s" -ForegroundColor Red
    }
}

function Assert-DefaultAutomaticRefresh {
    param(
        [string] $Case,
        [string] $Text
    )

    Assert-CaseMatch $Case $Text "automatic refresh callback" '(?m)^\[refresh\] succeeded at ' | Out-Null
    Assert-CaseMatch $Case $Text "connection remained stable" '(?m)^\[status\] connectionId=' | Out-Null
    Assert-CaseNoMatch $Case $Text "no reconnect during automatic refresh" '^\[conn\] RECONNECT(?:ING|ED)' | Out-Null
    $snapshots = @(Get-ClaimSnapshots $Text)
    $callbackObserved = $snapshots.Count -ge 2 `
        -and $snapshots[-1]["marker"] -ne $snapshots[0]["marker"] `
        -and $snapshots[-1]["__defaultRefreshCount"] -gt $snapshots[0]["__defaultRefreshCount"] `
        -and -not [string]::IsNullOrEmpty($snapshots[-1]["__defaultRefreshExpiration"])
    if ($callbackObserved) {
        Write-Host "  PASS  automatic refresh updated principal and expiration"
    }
    else {
        $failures.Add("$Case`: automatic refresh did not update principal and expiration")
        Write-Host "  FAIL  automatic refresh updated principal and expiration" -ForegroundColor Red
    }
    Assert-ServiceTokenExpirationAdvanced $Case $Text
}

function Assert-MinimumRefreshCount {
    param(
        [string] $Case,
        [string] $Text,
        [int] $Minimum
    )

    $count = [regex]::Matches($Text, '(?m)^\[refresh\] succeeded at ').Count
    if ($count -ge $Minimum) {
        Write-Host "  PASS  at least $Minimum automatic refresh cycle(s)"
    }
    else {
        $failures.Add("$Case`: expected at least $Minimum automatic refresh cycles, found $count")
        Write-Host "  FAIL  at least $Minimum automatic refresh cycle(s)" -ForegroundColor Red
    }
}

function Assert-ReconnectedWithNewId {
    param(
        [string] $Case,
        [string] $Text
    )

    $initial = [regex]::Match($Text, '(?m)^\[conn\] connected: (.+)$').Groups[1].Value.Trim()
    $reconnected = [regex]::Match($Text, '(?m)^\[conn\] RECONNECTED with new connectionId=(.+)$').Groups[1].Value.Trim()
    if (-not [string]::IsNullOrEmpty($initial) -and -not [string]::IsNullOrEmpty($reconnected) -and $initial -ne $reconnected) {
        Write-Host "  PASS  reconnect created a new connection id"
    }
    else {
        $failures.Add("$Case`: reconnect did not produce a distinct connection id")
        Write-Host "  FAIL  reconnect created a new connection id" -ForegroundColor Red
    }
}

function Assert-ConnectionIdStable {
    param(
        [string] $Case,
        [string] $Text
    )

    $initial = [regex]::Match($Text, '(?m)^\[conn\] connected: (.+)$').Groups[1].Value.Trim()
    $final = [regex]::Match($Text, '(?m)^\[status\] connectionId=(.+)$').Groups[1].Value.Trim()
    if (-not [string]::IsNullOrEmpty($initial) -and $final -eq $initial) {
        Write-Host "  PASS  connection id remained stable"
    }
    else {
        $failures.Add("$Case`: final connection id '$final' did not match initial id '$initial'")
        Write-Host "  FAIL  connection id remained stable" -ForegroundColor Red
    }
}

function Assert-ConnectionOpenThroughStatus {
    param(
        [string] $Case,
        [string] $Text
    )

    $markerIndex = $Text.IndexOf("[marker] server-side Context.User marker=", [StringComparison]::Ordinal)
    $statusIndex = $Text.IndexOf("[status] connectionId=", [StringComparison]::Ordinal)
    $closedIndex = $Text.IndexOf("[conn] closed", [StringComparison]::Ordinal)
    if ($markerIndex -ge 0 -and $statusIndex -gt $markerIndex -and ($closedIndex -lt 0 -or $closedIndex -gt $statusIndex)) {
        Write-Host "  PASS  connection remained open through post-expiry status"
    }
    else {
        $failures.Add("$Case`: connection closed before the post-expiry marker and status checks completed")
        Write-Host "  FAIL  connection remained open through post-expiry status" -ForegroundColor Red
    }
}

function Test-ConnectedRefreshCase {
    param(
        [string] $Name,
        [string] $Output,
        [string] $LifetimePattern = "\d+"
    )

    Assert-CaseMatch $Name $Output "connected" '(?m)^\[conn\] connected: .+$' | Out-Null
    Assert-CaseMatch $Name $Output "negotiate lifetime" "\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=$LifetimePattern; jwtLifetime=$LifetimePattern" | Out-Null
    Assert-CaseMatch $Name $Output "manual refresh" "\[http\] /hub/refresh => 200; tokenLifetimeSeconds=$LifetimePattern; jwtLifetime=$LifetimePattern" | Out-Null
    if ([regex]::IsMatch($Output, '(?m)^\[conn\] RECONNECT|^\[conn\] RECONNECTED')) {
        $failures.Add("$Name`: connection reconnected during in-place refresh")
        Write-Host "  FAIL  no reconnect" -ForegroundColor Red
    }
    else {
        Write-Host "  PASS  no reconnect"
    }
}

New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null

if ($RuntimeUpstreamOnly) {
    $IncludeRuntimeUpstream = $true
    $SkipDefaultMode = $true
}

if ($IncludeRuntimeUpstream -and $SkipServerlessMode) {
    throw "-IncludeRuntimeUpstream requires Serverless mode. Remove -SkipServerlessMode."
}

if (-not $SkipDefaultMode) {
    Assert-DefaultConfiguration
}

if (-not $SkipBuild) {
    Write-Host "Building matrix projects..."
    if (-not $SkipDefaultMode) { Invoke-DotNetBuild $defaultProject }
    if (-not $SkipServerlessMode) {
        $functionBuildArguments = if ($IncludeRuntimeUpstream) { @("-p:EnableUpstreamFunctions=true") } else { @() }
        Invoke-DotNetBuild $functionProject $functionBuildArguments
    }
    Invoke-DotNetBuild $clientProject
    if ($IncludeCompatibility) { Invoke-DotNetBuild $oldSdkProject }
    if (-not $SkipDefaultMode) {
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
}

$hostProcess = $null
try {
    if (-not $SkipDefaultMode) {
        Write-Host "`n=== Default mode matrix ==="
        $hostProcess = Start-DefaultHost "default-standard" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=true",
            "--AuthRefresh:GateEnabled=true"
        )

        foreach ($transport in @("ws", "sse", "lp")) {
            $name = "default-token-$transport"
            $output = Invoke-ClientCase $name $ServerUrl $transport "token" "standard" "on" @(
                "/claims", "/marker", "/refresh", "/claims", "/marker", "/wait 1100", "/refresh", "/claims",
                "/refresh-as alice blocked", "/refresh-as bob user", "/status"
            )
            Test-ConnectedRefreshCase $name $output
            Assert-ClaimChange $name $output "Changed" "Changed"
            Assert-CaseMatch $name $output "gate and user mismatch rejected" '\[http\] /hub/refresh => 403;.*permission_change_rejected' 2 | Out-Null
            $markers = @([regex]::Matches($output, '(?m)^\[marker\] server-side Context\.User marker=(.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
            if ($markers.Count -lt 2 -or $markers[0] -eq $markers[1]) {
                $failures.Add("$name`: marker did not change")
                Write-Host "  FAIL  marker changed" -ForegroundColor Red
            }
            else { Write-Host "  PASS  marker changed" }
        }

        if ($IncludeAutoRefresh) {
            Write-Host "`n--- Default automatic refresh ---"
            foreach ($transport in @("ws", "sse", "lp")) {
                $name = "default-auto-dotnet-$transport"
                $output = Invoke-ClientCase $name $ServerUrl $transport "token" "standard" "on" @(
                    "/claims", "/wait 30000", "/wait 10000", "/claims", "/status"
                ) 45 1 "on" "off" 10
                Assert-DefaultAutomaticRefresh $name $output
            }

            foreach ($transport in @("ws", "sse", "lp")) {
                $name = "default-auto-typescript-$transport"
                $output = Invoke-TypeScriptClientCase $name $ServerUrl $transport "on" 1 45 10000
                Assert-CaseMatch $name $output "automatic refresh retained transport" '(?m)^\[pass\] TypeScript automatic refresh ran \d+ time\(s\), retained connection .+' | Out-Null
                Assert-CaseNoMatch $name $output "automatic refresh did not fail" '^\[refresh:failed\]' | Out-Null
            }

            $name = "default-auto-short-lifetime-boundary"
            $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/wait 25000") 20 1 "on" "off" 5
            Assert-CaseNoMatch $name $output "short lifetime did not reach the refresh timer" '^\[refresh\] succeeded at |^\[http\] /hub/refresh =>' | Out-Null
            Assert-CaseMatch $name $output "authentication deadline closed the connection" '(?m)^\[conn\] closed at ' | Out-Null
            Assert-CaseNoMatch $name $output "automatic reconnect stayed disabled" '^\[conn\] RECONNECT(?:ING|ED)' | Out-Null
        }

        foreach ($transport in @("ws", "sse", "lp")) {
            $name = "default-typescript-$transport"
            $output = Invoke-TypeScriptClientCase $name $ServerUrl $transport
            Assert-CaseMatch $name $output "connected" '(?m)^\[conn\] connected: .+$' | Out-Null
            Assert-CaseMatch $name $output "refresh retained transport" '(?m)^\[pass\] TypeScript refresh retained connection .+' | Out-Null
        }

        $name = "default-cookie-ws"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "cookie" "standard" "on" @(
            "/claims", "/marker", "/wait 1100", "/refresh", "/claims", "/marker"
        )
        Test-ConnectedRefreshCase $name $output
        Assert-DefaultClaimsPreserved $name $output
        Assert-ServiceTokenExpirationAdvanced $name $output
        $markers = @([regex]::Matches($output, '(?m)^\[marker\] server-side Context\.User marker=(.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
        if ($markers.Count -ge 2 -and $markers[0] -eq $markers[1]) { Write-Host "  PASS  cookie claims preserved" }
        else { $failures.Add("$name`: cookie marker changed"); Write-Host "  FAIL  cookie claims preserved" -ForegroundColor Red }

        $name = "default-same-user-role-change"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/refresh-as alice admin", "/status")
        Test-ConnectedRefreshCase $name $output
        Assert-CaseMatch $name $output "same-user role updated" '(?m)^\[status\] principal=.*"userIdentifier":"alice".*"role":"admin"' | Out-Null
        Assert-CaseMatch $name $output "role claim type preserved" '(?m)^\[status\] principal=.*"roleClaimType":"role".*"isInRole":true' | Out-Null

        $name = "default-endpoint-errors"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/probe get", "/probe missing-header", "/probe query-only", "/probe empty-header",
            "/probe no-auth", "/probe invalid-auth", "/probe unknown", "/refresh-invalid", "/probe header-and-query"
        )
        Assert-CaseMatch $name $output "GET rejected" '(?m)^\[probe:get\] 405 MethodNotAllowed;' | Out-Null
        Assert-CaseMatch $name $output "missing connection header rejected" '(?m)^\[probe:missing-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "query-only token rejected" '(?m)^\[probe:query-only\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "empty connection header rejected" '(?m)^\[probe:empty-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "missing auth rejected" '(?m)^\[probe:no-auth\] 401 Unauthorized;' | Out-Null
        Assert-CaseMatch $name $output "invalid auth rejected" '(?m)^\[probe:invalid-auth\] 401 Unauthorized;' | Out-Null
        Assert-CaseMatch $name $output "unknown connection rejected" '(?m)^\[probe:unknown\] 404 NotFound;.*connection_not_found' | Out-Null
        Assert-CaseMatch $name $output "invalid refresh token rejected" '\[http\] /hub/refresh => 401;' | Out-Null
        Assert-CaseMatch $name $output "header wins over unknown query token" '(?m)^\[probe:header-and-query\] 200 OK; body=.*"accessToken":"[^"]+".*"tokenLifetimeSeconds":\d+' | Out-Null

        $name = "default-rejected-state"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh-as alice blocked", "/claims", "/refresh-as bob user", "/claims", "/status"
        )
        Assert-CaseMatch $name $output "both rejected refreshes returned 403" '\[http\] /hub/refresh => 403;.*permission_change_rejected' 2 | Out-Null
        Assert-RejectedRefreshPreservedState $name $output
        Assert-CaseMatch $name $output "connection remained usable" '(?m)^\[status\] connectionId=' | Out-Null

        $name = "default-concurrent-refresh"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/claims", "/parallel 10", "/claims")
        Assert-CaseMatch $name $output "ten parallel refresh requests completed" '\[http\] /hub/refresh => 200;' 10 | Out-Null
        Assert-CaseNoMatch $name $output "parallel refreshes were not rejected" '^\[refresh:parallel-\d+\] rejected:' | Out-Null
        Assert-RefreshCount $name $output 10

        $name = "default-mixed-lifetime-concurrency"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/claims", "/parallel-lifetimes 30,120,45,90", "/claims", "/status"
        )
        Assert-CaseMatch $name $output "mixed-lifetime refreshes completed" '\[http\] /hub/refresh => 200;' 4 | Out-Null
        Assert-CaseNoMatch $name $output "mixed-lifetime refreshes were not rejected" '^\[refresh:parallel-lifetime-\d+\] rejected:' | Out-Null
        Assert-RefreshCount $name $output 4
        Assert-MixedLifetimeDeadline $name $output

        $name = "default-stale-deadline"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh-lifetime 120", "/wait 1100", "/refresh-lifetime 30", "/claims"
        )
        Test-ConnectedRefreshCase $name $output
        Assert-ClaimChange $name $output "Changed" "Preserved"

        $name = "default-negotiate-v0"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/refresh", "/status") 60 0
        Assert-CaseMatch $name $output "connected with negotiate v0" '(?m)^\[conn\] connected: .+$' | Out-Null
        Assert-CaseMatch $name $output "v0 refresh rejected" '\[http\] /hub/refresh => 404;.*connection_not_found' | Out-Null
        Assert-CaseMatch $name $output "v0 connection remained usable" '(?m)^\[status\] connectionId=' | Out-Null

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-no-gate" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=true",
            "--AuthRefresh:GateEnabled=false"
        )
        $name = "default-no-gate-role-change"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/refresh-as alice blocked", "/status")
        Test-ConnectedRefreshCase $name $output
        Assert-CaseMatch $name $output "blocked role accepted without application gate" '(?m)^\[status\] principal=.*"userIdentifier":"alice".*"role":"blocked"' | Out-Null

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-throwing-gate" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=true",
            "--AuthRefresh:GateEnabled=true",
            "--AuthRefresh:ThrowInGate=true"
        )
        $name = "default-throwing-gate"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh", "/claims", "/refresh", "/claims", "/status"
        )
        Assert-CaseMatch $name $output "gate exceptions returned controlled errors" '\[http\] /hub/refresh => 500;.*internal_server_error' 2 | Out-Null
        Assert-RejectedRefreshPreservedState $name $output
        Assert-CaseMatch $name $output "connection remained usable" '(?m)^\[status\] connectionId=' | Out-Null

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-maximum-expiration" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=true",
            "--AuthRefresh:GateEnabled=true",
            "--AuthRefresh:ServiceTokenLifetimeSeconds=300",
            "--AuthRefresh:MaximumAuthenticationExpirationSeconds=30"
        )
        foreach ($expirationMode in @("on", "off")) {
            $name = "default-maximum-expiration-$expirationMode"
            $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" $expirationMode @(
                "/claims", "/refresh", "/wait 1100", "/refresh", "/claims"
            ) 120
            Test-ConnectedRefreshCase $name $output
            Assert-ClaimChange $name $output "Changed" "Changed"
            Assert-TokenLifetimeAtMost $name $output 30
        }

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-close-disabled" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:CloseOnAuthenticationExpiration=false",
            "--AuthRefresh:GateEnabled=true",
            "--AuthRefresh:ServiceTokenLifetimeSeconds=30"
        )
        $name = "default-close-disabled"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh", "/wait 1100", "/refresh", "/claims", "/status"
        ) 120
        Test-ConnectedRefreshCase $name $output
        Assert-ClaimChange $name $output "Changed" "Absent"
        Assert-TokenLifetimeAtMost $name $output 30

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-disabled" @(
            "--AuthRefresh:Enabled=false",
            "--AuthRefresh:ServiceTokenLifetimeSeconds=300"
        )
        $name = "default-refresh-disabled"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @("/refresh")
        Assert-CaseMatch $name $output "connected" '(?m)^\[conn\] connected: .+$' | Out-Null
        Assert-CaseMatch $name $output "lifetime omitted" '\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=omitted; jwtLifetime=300s' | Out-Null
        Assert-CaseMatch $name $output "refresh endpoint unavailable" '\[http\] /hub/refresh => 404; body=' | Out-Null
        Assert-CaseMatch $name $output "manual refresh rejected" '(?m)^\[refresh:manual\] rejected:.*404 \(Not Found\)' | Out-Null

        Stop-ProcessTree $hostProcess
        $hostProcess = Start-DefaultHost "default-no-expiry" @(
            "--AuthRefresh:Enabled=true",
            "--AuthRefresh:RequireNullExpiration=true",
            "--AuthRefresh:ServiceTokenLifetimeSeconds=300"
        )
        $name = "default-no-app-expiry"
        $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "off" @(
            "/claims", "/refresh", "/wait 1100", "/refresh", "/claims"
        )
        Test-ConnectedRefreshCase $name $output '\d+'
        Assert-ClaimChange $name $output "Changed" "Absent"

        Stop-ProcessTree $hostProcess
        $hostProcess = $null

        Write-Host "`n--- Default cross-app-server type-43 routing ---"
        $appAProcess = $null
        $appBProcess = $null
        $proxyProcess = $null
        try {
            $crossServerOptions = @(
                "--AuthRefresh:Enabled=true",
                "--AuthRefresh:CloseOnAuthenticationExpiration=true",
                "--AuthRefresh:GateEnabled=true",
                "--Azure:SignalR:ServerStickyMode=Required"
            )
            $appAProcess = Start-DefaultHost "default-cross-server-app-a" $crossServerOptions $CrossServerAppAUrl
            $appBProcess = Start-DefaultHost "default-cross-server-app-b" $crossServerOptions $CrossServerAppBUrl
            $proxyProcess = Start-RefreshProxy

            $name = "default-cross-server-type43"
            $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
                "/marker", "/refresh-as alice admin", "/marker", "/status"
            )
            Test-ConnectedRefreshCase $name $output
            Assert-CaseMatch $name $output "owning app server observed refreshed role" '(?m)^\[status\] principal=.*"role":"admin"' | Out-Null
            Assert-CaseMatch $name $output "owning app server callback observed type-43 update" 'Authentication refreshed on the live connection; still alice \(marker=' | Out-Null
            Assert-CaseNoMatch $name $output "cross-server refresh did not reconnect" '^\[conn\] RECONNECT(?:ING|ED)' | Out-Null

            $proxyLog = Get-Content (Join-Path $artifactsDirectory "default-cross-server.proxy.stdout.log") -Raw
            Assert-CaseMatch $name $proxyLog "token and negotiate routed to app A" 'POST /(?:token|hub/negotiate).* -> A' 2 | Out-Null
            Assert-CaseMatch $name $proxyLog "refresh routed to app B" 'POST /hub/refresh.* -> B' | Out-Null
        }
        finally {
            Stop-ProcessTree $proxyProcess
            Stop-ProcessTree $script:hostProcess
            Stop-ProcessTree $appBProcess
            Stop-ProcessTree $appAProcess
            $script:hostProcess = $null
        }

        if ($IncludeCompatibility) {
            Write-Host "`n--- Old SDK compatibility ---"
            $hostProcess = Start-OldSdkHost
            $name = "compatibility-old-sdk-no-advertisement"
            $output = Invoke-ClientCase $name $OldSdkUrl "ws" "token" "standard" "on" @("/wait 15000", "/status") 10 1 "on" "off" 2
            Assert-CaseMatch $name $output "connected through old SDK" '(?m)^\[conn\] connected: .+$' | Out-Null
            Assert-CaseMatch $name $output "old SDK omitted refresh lifetime" '\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=omitted;' | Out-Null
            Assert-CaseNoMatch $name $output "new client did not schedule refresh" '^\[refresh\] succeeded at |^\[http\] /hub/refresh =>' | Out-Null
            Assert-CaseMatch $name $output "connection remained usable past app token expiration" '(?m)^\[status\] connectionId=' | Out-Null
            Stop-ProcessTree $hostProcess
            $hostProcess = $null
        }

        if ($IncludeLongRunning) {
            Write-Host "`n--- Long-running continuity ---"
            $hostProcess = Start-DefaultHost "default-continuity" @(
                "--AuthRefresh:Enabled=true",
                "--AuthRefresh:CloseOnAuthenticationExpiration=true",
                "--AuthRefresh:GateEnabled=true",
                "--AuthRefresh:ServiceTokenLifetimeSeconds=45",
                "--Tick:Enabled=true",
                "--Tick:IntervalSeconds=1"
            )

            $name = "long-running-refresh-continuity"
            $output = Invoke-ClientCase $name $ServerUrl "ws" "token" "standard" "on" @(
                "/wait 30000", "/wait 30000", "/wait 20000", "/status"
            ) 45 1 "on" "off" 10
            Assert-MinimumRefreshCount $name $output 2
            Assert-CaseMatch $name $output "tick stream remained active" '(?m)^\[tick\] #' 60 | Out-Null
            Assert-CaseNoMatch $name $output "no tick loss with refresh" '^\[LOST\]' | Out-Null
            Assert-CaseNoMatch $name $output "no reconnect with refresh" '^\[conn\] RECONNECT(?:ING|ED)' | Out-Null

            $name = "long-running-no-refresh-control"
            $output = Invoke-ClientCase -Name $name -BaseUrl $ServerUrl -Transport "ws" -AuthMode "token" `
                -Scenario "standard" -Expiration "on" -Commands @("/wait 18000", "/status") `
                -LifetimeSeconds 10 -NegotiateVersion 1 -AutoRefresh "off" -AutoReconnect "on" `
                -ReconnectDelaySeconds 5
            Assert-CaseMatch $name $output "connection entered reconnecting" '(?m)^\[conn\] RECONNECTING' | Out-Null
            Assert-CaseMatch $name $output "connection reconnected" '(?m)^\[conn\] RECONNECTED with new connectionId=' | Out-Null
            Assert-ReconnectedWithNewId $name $output
            Assert-CaseMatch $name $output "tick loss exposed reconnect gap" '(?m)^\[LOST\]' | Out-Null

            foreach ($transport in @("sse", "lp")) {
                $name = "refreshed-token-adoption-$transport"
                $output = Invoke-ClientCase -Name $name -BaseUrl $ServerUrl -Transport $transport `
                    -AuthMode "token" -Scenario "standard" -Expiration "on" -Commands @(
                        "/wait 15000", "/refresh", "/wait 30000", "/wait 2000", "/marker", "/status"
                    ) -LifetimeSeconds 60 -NegotiateVersion 1 -AutoRefresh "off" -AutoReconnect "off"
                Test-ConnectedRefreshCase $name $output '4[45]'
                Assert-CaseMatch $name $output "hub invocation succeeded after original token expiration" `
                    '(?m)^\[marker\] server-side Context\.User marker=.+$' | Out-Null
                Assert-ConnectionIdStable $name $output
                Assert-ConnectionOpenThroughStatus $name $output
            }

            Stop-ProcessTree $hostProcess
            $hostProcess = $null
        }
    }

    if (-not $SkipServerlessMode) {
        Write-Host "`n=== Serverless mode matrix ==="

        if (-not $RuntimeUpstreamOnly) {
            Write-Host "`n--- Serverless Transient REST lane ---"
            $hostProcess = Start-ServerlessHost -ServiceTransportType "Transient"

            foreach ($case in @(
                @{ Name = "serverless-transient-standard"; Scenario = "standard"; Marker = "Changed"; Deadline = "Changed" },
                @{ Name = "serverless-transient-claims-only"; Scenario = "claims-only"; Marker = "Changed"; Deadline = "Preserved" }
            )) {
                $refreshCommand = if ($case.Deadline -eq "Changed") { "/refresh-lifetime 120" } else { "/refresh" }
                $output = Invoke-ClientCase $case.Name $FunctionUrl "ws" "token" $case.Scenario "on" @("/claims", $refreshCommand, "/claims")
                Test-ConnectedRefreshCase $case.Name $output
                Assert-ClaimChange $case.Name $output $case.Marker $case.Deadline
            }

            $name = "serverless-transient-rejected-policy"
            $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @(
                "/claims", "/refresh-as alice blocked", "/claims"
            )
            Assert-CaseMatch $name $output "REST policy rejected blocked role" '\[http\] /hub/refresh => 403;.*permission_change_rejected' | Out-Null
            Assert-ClaimChange $name $output "Preserved" "Preserved"

            $name = "serverless-transient-endpoint-errors"
            $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @(
                "/probe missing-header", "/probe query-only", "/probe empty-header",
                "/probe no-auth", "/probe invalid-auth", "/probe unknown", "/probe header-and-query"
            )
            Assert-CaseMatch $name $output "REST missing connection header rejected" '(?m)^\[probe:missing-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
            Assert-CaseMatch $name $output "REST query-only token rejected" '(?m)^\[probe:query-only\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
            Assert-CaseMatch $name $output "REST empty connection header rejected" '(?m)^\[probe:empty-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
            Assert-CaseMatch $name $output "REST missing auth rejected" '(?m)^\[probe:no-auth\] 401 Unauthorized;' | Out-Null
            Assert-CaseMatch $name $output "REST invalid auth rejected" '(?m)^\[probe:invalid-auth\] 401 Unauthorized;' | Out-Null
            Assert-CaseMatch $name $output "REST unknown connection rejected" '(?m)^\[probe:unknown\] 404 NotFound;.*refresh_failed' | Out-Null
            Assert-CaseMatch $name $output "REST header wins over unknown query token" '(?m)^\[probe:header-and-query\] 200 OK; body=.*"accessToken":"[^"]+".*"tokenLifetimeSeconds":\d+' | Out-Null

            Stop-ProcessTree $hostProcess
            $hostProcess = $null
        }

        $hostProcess = Start-ServerlessHost -ServiceTransportType "Persistent"

        if ($IncludeRuntimeUpstream) {
            Write-Host "`n--- OSS Runtime + local upstream ---"
            $endpointCases = [System.Collections.Generic.List[hashtable]]::new()
            if ([string]::IsNullOrWhiteSpace($SecondaryRuntimeConnectionString)) {
                $endpointCases.Add(@{ Name = $null; ConnectionString = $RuntimeConnectionString })
            }
            else {
                $endpointCases.Add(@{ Name = "Endpoint1"; ConnectionString = $RuntimeConnectionString })
                $endpointCases.Add(@{ Name = "Endpoint2"; ConnectionString = $SecondaryRuntimeConnectionString })
            }

            for ($endpointIndex = 0; $endpointIndex -lt $endpointCases.Count; $endpointIndex++) {
                $endpointCase = $endpointCases[$endpointIndex]
                $suffix = if ($endpointCases.Count -eq 1) { "" } else { "-endpoint$($endpointIndex + 1)-owner" }
                $name = "serverless-runtime-upstream-refresh-claims$suffix"
                $beforeMessage = "before-refresh-$endpointIndex-$runId"
                $afterMessage = "after-refresh-$endpointIndex-$runId"
                $output = Invoke-ClientCase -Name $name -BaseUrl $FunctionUrl -Transport "ws" `
                    -AuthMode "token" -Scenario "standard" -Expiration "on" -Commands @(
                        "/marker", $beforeMessage, "/wait 2000", "/refresh", "/marker", $afterMessage, "/wait 2000", "/status"
                    ) -EndpointName $endpointCase.Name
                $functionLog = Get-Content (Join-Path $artifactsDirectory "serverless.host.stdout.log") -Raw
                Assert-UpstreamRefreshCase $name $output $functionLog $beforeMessage $afterMessage
                Assert-NegotiatedEndpoint $name $output $endpointCase.ConnectionString
            }
        }

        if (-not $RuntimeUpstreamOnly) {
        $positiveCases = @(
            @{ Name = "serverless-standard"; Scenario = "standard"; Marker = "Changed"; Deadline = "Changed"; Lifetime = "\d+" },
            @{ Name = "serverless-expiration-only"; Scenario = "expiration-only"; Marker = "Preserved"; Deadline = "Changed"; Lifetime = "\d+" },
            @{ Name = "serverless-claims-only"; Scenario = "claims-only"; Marker = "Changed"; Deadline = "Preserved"; Lifetime = "\d+" },
            @{ Name = "serverless-renewal-only"; Scenario = "renewal-only"; Marker = "Preserved"; Deadline = "Absent"; Lifetime = "60" },
            @{ Name = "serverless-non-utc"; Scenario = "non-utc-expiration"; Marker = "Changed"; Deadline = "Changed"; Lifetime = "\d+" },
            @{ Name = "serverless-max-value"; Scenario = "max-value-expiration"; Marker = "Changed"; Deadline = "Preserved"; Lifetime = "\d+" },
            @{ Name = "serverless-close-disabled-max"; Scenario = "close-disabled-max-value"; Marker = "Changed"; Deadline = "Absent"; Lifetime = "\d+" },
            @{ Name = "serverless-token-max-wins"; Scenario = "token-maximum-wins"; Marker = "Changed"; Deadline = "Changed"; Lifetime = "30" }
        )

        foreach ($case in $positiveCases) {
            $refreshCommand = if ($case.Deadline -eq "Changed") { "/refresh-lifetime 120" } else { "/refresh" }
            $output = Invoke-ClientCase $case.Name $FunctionUrl "ws" "token" $case.Scenario "on" @("/claims", $refreshCommand, "/claims")
            Test-ConnectedRefreshCase $case.Name $output $case.Lifetime
            Assert-ClaimChange $case.Name $output $case.Marker $case.Deadline
        }

        foreach ($scenario in @("deadline-omitted", "deadline-max-value", "close-disabled")) {
            $name = "serverless-$scenario"
            $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" $scenario "on" @("/claims", "/refresh", "/claims")
            Test-ConnectedRefreshCase $name $output
        }

        $name = "serverless-past-refresh"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "past-refresh-expiration" "on" @("/refresh")
        Assert-CaseMatch $name $output "connected" '(?m)^\[conn\] connected: .+$' | Out-Null
        Assert-CaseMatch $name $output "invalid expiration rejected" '\[http\] /hub/refresh => 400;.*invalid_expiration' | Out-Null

        $name = "serverless-rejected-policy"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh-as alice blocked", "/claims"
        )
        Assert-CaseMatch $name $output "serverless policy rejected blocked role" '\[http\] /hub/refresh => 403;.*permission_change_rejected' | Out-Null
        Assert-ClaimChange $name $output "Preserved" "Preserved"

        $name = "serverless-user-mismatch"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @(
            "/claims", "/refresh-as bob user", "/claims"
        )
        Assert-CaseMatch $name $output "serverless runtime rejected user mismatch" '\[http\] /hub/refresh => 403;.*refresh_failed' | Out-Null
        Assert-ClaimChange $name $output "Preserved" "Preserved"

        $name = "serverless-endpoint-errors"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @(
            "/probe missing-header", "/probe query-only", "/probe empty-header",
            "/probe no-auth", "/probe invalid-auth", "/probe unknown", "/probe header-and-query"
        )
        Assert-CaseMatch $name $output "serverless missing connection header rejected" '(?m)^\[probe:missing-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "serverless query-only token rejected" '(?m)^\[probe:query-only\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "serverless empty connection header rejected" '(?m)^\[probe:empty-header\] 400 BadRequest; body=.*"error":"missing_connection_token"' | Out-Null
        Assert-CaseMatch $name $output "serverless missing auth rejected" '(?m)^\[probe:no-auth\] 401 Unauthorized;' | Out-Null
        Assert-CaseMatch $name $output "serverless invalid auth rejected" '(?m)^\[probe:invalid-auth\] 401 Unauthorized;' | Out-Null
        Assert-CaseMatch $name $output "serverless unknown connection rejected" '(?m)^\[probe:unknown\] 404 NotFound;.*refresh_failed' | Out-Null
        Assert-CaseMatch $name $output "serverless header wins over unknown query token" '(?m)^\[probe:header-and-query\] 200 OK; body=.*"accessToken":"[^"]+".*"tokenLifetimeSeconds":\d+' | Out-Null

        foreach ($negative in @(
            @{ Scenario = "past-negotiate-deadline"; Error = "expired_authentication_deadline" }
        )) {
            $name = "serverless-$($negative.Scenario)"
            $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" $negative.Scenario "on" @()
            Assert-CaseMatch $name $output "negotiate rejected" "\[http\] /hub/negotiate => 400;.*$($negative.Error)" | Out-Null
            Assert-CaseMatch $name $output "connection failed" '(?m)^\[conn\] START FAILED:' | Out-Null
        }

        $name = "serverless-lifetime-api-disabled"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "lifetime-api-disabled" "on" @()
        Assert-CaseMatch $name $output "lifetime API accepts refresh opt-out" '\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=\d+; jwtLifetime=\d+s' | Out-Null
        Assert-CaseMatch $name $output "connected without refresh opt-in" '(?m)^\[conn\] connected: .+$' | Out-Null

        $name = "serverless-lifetime-api-null-options"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "lifetime-api-null-options" "on" @()
        Assert-CaseMatch $name $output "null options use one-hour defaults" '\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=3600; jwtLifetime=3600s' | Out-Null
        Assert-CaseMatch $name $output "connected with default options" '(?m)^\[conn\] connected: .+$' | Out-Null

        $name = "serverless-ordinary-negotiate"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "ordinary-negotiate" "on" @("/claims")
        Assert-CaseMatch $name $output "connected" '(?m)^\[conn\] connected: .+$' | Out-Null
        Assert-CaseMatch $name $output "lifetime omitted" '\[http\] /hub/negotiate => 200; tokenLifetimeSeconds=omitted;' | Out-Null

        $name = "serverless-negotiate-v0"
        $output = Invoke-ClientCase $name $FunctionUrl "ws" "token" "standard" "on" @("/refresh", "/claims") 60 0
        Assert-CaseMatch $name $output "connected with negotiate v0" '(?m)^\[conn\] connected: .+$' | Out-Null
        Assert-CaseMatch $name $output "v0 refresh rejected" '\[http\] /hub/refresh => 404;.*refresh_failed' | Out-Null
        Assert-CaseMatch $name $output "v0 claims lookup unavailable" '(?m)^\[claims\] no negotiated connection token was captured$' | Out-Null
        }
    }
}
finally {
    Stop-ProcessTree $hostProcess
}

Write-Host "`n=== Matrix summary ==="
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "FAIL  $failure" -ForegroundColor Red
    }
    Write-Host "Logs: $artifactsDirectory"
    exit 1
}

Write-Host "PASS  All automated matrix cases passed."
Write-Host "Logs: $artifactsDirectory"
$notAutomated = @("Windows auth", "AAD data actions", "multi-owner sharding", "runtime feature-flag toggling", "cross-pod fault injection")
if (-not $IncludeRuntimeUpstream) { $notAutomated += "OSS Runtime + local upstream (run with -IncludeRuntimeUpstream)" }
Write-Host "Not automated in this run: $($notAutomated -join ', ')."
