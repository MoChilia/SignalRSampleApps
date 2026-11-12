[CmdletBinding()]
param(
    [string] $ConnectionString = $env:Azure__SignalR__ConnectionString
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Base64Url {
    param([byte[]] $Bytes)

    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-AzureSignalRToken {
    param(
        [string] $Audience,
        [string] $AccessKey
    )

    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{
        alg = 'HS256'
        typ = 'JWT'
    } | ConvertTo-Json -Compress)))
    $payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{
        iss = 'azure-signalr'
        aud = $Audience
        iat = $now
        nbf = $now
        exp = $now + 300
    } | ConvertTo-Json -Compress)))
    $unsignedToken = "$header.$payload"

    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($AccessKey))
    try {
        $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsignedToken)))
    }
    finally {
        $hmac.Dispose()
    }

    return "$unsignedToken.$signature"
}

function ConvertFrom-ConnectionString {
    param([string] $Value)

    $properties = @{}
    foreach ($part in $Value.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        $separator = $part.IndexOf('=')
        if ($separator -le 0) {
            throw "Invalid Azure SignalR connection string property: '$part'."
        }

        $properties[$part.Substring(0, $separator).Trim().ToLowerInvariant()] = $part.Substring($separator + 1).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($properties.endpoint) -or
        [string]::IsNullOrWhiteSpace($properties.accesskey)) {
        throw 'The Azure SignalR connection string must contain Endpoint and AccessKey.'
    }

    $endpoint = [UriBuilder]::new($properties.endpoint)
    if ($properties.port) {
        $endpoint.Port = [int] $properties.port
    }

    return @{
        Endpoint = $endpoint.Uri.AbsoluteUri.TrimEnd('/')
        AccessKey = $properties.accesskey
    }
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'Pass -ConnectionString or set Azure__SignalR__ConnectionString.'
}

$service = ConvertFrom-ConnectionString $ConnectionString
$testCases = @(
    @{ Name = 'Token A'; Audience = '/client/?hub=hub'; RequestHub = 'hubABC' }
    @{ Name = 'Token B'; Audience = '/client/?hub=hubABC'; RequestHub = 'hub' }
    @{ Name = 'Token C'; Audience = '/client/?hub=zzz'; RequestHub = 'hub' }
)

$httpClient = [Net.Http.HttpClient]::new()
try {
    $controlHub = 'hub'
    $controlAudiencePath = "/client/?hub=$controlHub"
    $controlAudience = "$($service.Endpoint)$controlAudiencePath"
    $controlToken = New-AzureSignalRToken -Audience $controlAudience -AccessKey $service.AccessKey
    $controlRequestUri = "$($service.Endpoint)/client/negotiate?hub=$controlHub"
    $controlRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $controlRequestUri)
    try {
        $controlRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $controlToken)
        $controlResponse = $httpClient.SendAsync($controlRequest).GetAwaiter().GetResult()
        try {
            $controlStatusCode = [int] $controlResponse.StatusCode
            if ($controlStatusCode -ne 200) {
                throw "Control failed: aud='$controlAudiencePath', request='$controlRequestUri', expected HTTP 200 but received $controlStatusCode ($($controlResponse.StatusCode))."
            }

            Write-Host "PASS Control: matching aud='$controlAudiencePath' was accepted with HTTP 200."
        }
        finally {
            $controlResponse.Dispose()
        }
    }
    finally {
        $controlRequest.Dispose()
    }

    foreach ($testCase in $testCases) {
        $tokenAudience = "$($service.Endpoint)$($testCase.Audience)"
        $token = New-AzureSignalRToken -Audience $tokenAudience -AccessKey $service.AccessKey
        $requestUri = "$($service.Endpoint)/client/negotiate?hub=$([Uri]::EscapeDataString($testCase.RequestHub))"
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $requestUri)
        try {
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
            $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
            try {
                $statusCode = [int] $response.StatusCode
                if ($statusCode -ne 401) {
                    throw "$($testCase.Name) failed: aud='$($testCase.Audience)', request='$requestUri', expected HTTP 401 but received $statusCode ($($response.StatusCode))."
                }

                Write-Host "PASS $($testCase.Name): aud='$($testCase.Audience)' was rejected for hub='$($testCase.RequestHub)' with HTTP 401."
            }
            finally {
                $response.Dispose()
            }
        }
        finally {
            $request.Dispose()
        }
    }
}
finally {
    $httpClient.Dispose()
}

Write-Host 'All Azure SignalR token audience mismatch tests passed.'