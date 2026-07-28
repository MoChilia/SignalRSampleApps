[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SignalRName,

    [Parameter(Mandatory = $true)]
    [string] $ResourceGroup,

    [ValidateSet('read', 'write', 'read-write', 'none')]
    [string] $ExpectedProfile = 'none',

    [string] $Hub = 'chat',

    [string] $ConnectionToken = 'unknown-connection-token'
)

$ErrorActionPreference = 'Stop'

$hostName = az signalr show `
    --name $SignalRName `
    --resource-group $ResourceGroup `
    --query hostName `
    --output tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($hostName)) {
    throw "Could not resolve Azure SignalR resource '$SignalRName' in resource group '$ResourceGroup'."
}

$endpoint = "https://$hostName"
Write-Host "Testing $endpoint as $((az account show --query user.name --output tsv))"
Write-Host "Expected profile: $ExpectedProfile"

dotnet run --project "$PSScriptRoot\RestApiAuthz.csproj" -- `
    --endpoint $endpoint `
    --hub $Hub `
    --connection-token $ConnectionToken `
    --expected-profile $ExpectedProfile

exit $LASTEXITCODE
