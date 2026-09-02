[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Subscription,

    [Parameter(Mandatory = $true)]
    [string] $ResourceGroup,

    [Parameter(Mandatory = $true)]
    [Alias('SignalRName')]
    [string] $NamePrefix,

    [ValidateSet('Free_F1', 'Standard_S1', 'Premium_P1')]
    [string] $Sku = 'Standard_S1',

    [ValidateSet('Classic', 'Default', 'Serverless')]
    [string] $ServiceMode = 'Default',

    [ValidateRange(1, 3600)]
    [int] $RetryDelaySeconds = 30,

    [ValidateRange(0, 1000)]
    [int] $MaxAttempts = 0
)

$ErrorActionPreference = 'Stop'

function Invoke-AzureCli {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & az @Arguments
    return $LASTEXITCODE
}

function New-AttemptName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Prefix,

        [Parameter(Mandatory = $true)]
        [int] $Suffix
    )

    return "$Prefix-$Suffix"
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI was not found. Install it and run az login before retrying.'
}

$location = az group show `
    --subscription $Subscription `
    --name $ResourceGroup `
    --query location `
    --output tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($location)) {
    throw "Could not find resource group '$ResourceGroup' in subscription '$Subscription'."
}

$attempt = 0
$deleteJobs = @()
$failedName = $null
$existingNames = az signalr list `
    --subscription $Subscription `
    --resource-group $ResourceGroup `
    --query '[].name' `
    --output tsv

if ($LASTEXITCODE -ne 0) {
    throw "Could not list Azure SignalR resources in resource group '$ResourceGroup'."
}

$usedSuffixes = $existingNames |
    Where-Object { $_ -match "^$([regex]::Escape($NamePrefix))-(\d+)$" } |
    ForEach-Object { [int] $Matches[1] }
$nextSuffix = if ($usedSuffixes) { ($usedSuffixes | Measure-Object -Maximum).Maximum + 1 } else { 1 }

Write-Host "Deploying Azure SignalR resources using prefix '$NamePrefix' in '$ResourceGroup' ($location)."
Write-Host "Retries: $(if ($MaxAttempts -eq 0) { 'unlimited' } else { $MaxAttempts })"

while ($MaxAttempts -eq 0 -or $attempt -lt $MaxAttempts) {
    $attempt++
    $currentName = New-AttemptName -Prefix $NamePrefix -Suffix ($nextSuffix + $attempt - 1)

    if (-not [string]::IsNullOrWhiteSpace($failedName)) {
        Write-Host "Attempt ${attempt}: deleting failed resource '$failedName' while creating '$currentName'."
        $deleteJobs += Start-Job -ScriptBlock {
            param($Subscription, $ResourceGroup, $Name)

            az signalr delete `
                --subscription $Subscription `
                --resource-group $ResourceGroup `
                --name $Name `
                --output none

            return $LASTEXITCODE
        } -ArgumentList $Subscription, $ResourceGroup, $failedName

        $failedName = $null
    }

    Write-Host "Attempt ${attempt}: creating '$currentName'."
    $createExitCode = Invoke-AzureCli -Arguments @(
        'signalr', 'create',
        '--subscription', $Subscription,
        '--resource-group', $ResourceGroup,
        '--name', $currentName,
        '--location', $location,
        '--sku', $Sku,
        '--service-mode', $ServiceMode,
        '--unit-count', '1',
        '--tags', 'created-by=deploy-signalr-script',
        '--output', 'none'
    )

    $provisioningState = az signalr show `
        --subscription $Subscription `
        --resource-group $ResourceGroup `
        --name $currentName `
        --query provisioningState `
        --output tsv `
        2>$null

    if ($createExitCode -eq 0 -and $LASTEXITCODE -eq 0 -and $provisioningState -eq 'Succeeded') {
        if ($deleteJobs.Count -gt 0) {
            Write-Host 'Creation succeeded; waiting for concurrent deletion requests to finish before final verification.'
            $deleteExitCodes = $deleteJobs | Wait-Job | Receive-Job
            $deleteJobs | Remove-Job
            $deleteJobs = @()

            if ($deleteExitCodes | Where-Object { $_ -ne 0 }) {
                Write-Warning 'At least one concurrent deletion request failed.'
            }

        }

        $hostName = az signalr show `
            --subscription $Subscription `
            --resource-group $ResourceGroup `
            --name $currentName `
            --query hostName `
            --output tsv

        $resourceId = "/subscriptions/$Subscription/resourceGroups/$ResourceGroup/providers/Microsoft.SignalRService/SignalR/$currentName"
        Write-Host "Deployment succeeded on attempt $attempt."
        Write-Host "Resource: $resourceId"
        Write-Host "Endpoint: https://$hostName"
        exit 0
    }

    Write-Warning "Attempt $attempt failed (CLI exit code $createExitCode, provisioning state '$provisioningState')."
    $failedName = $currentName

    if ($MaxAttempts -eq 0 -or $attempt -lt $MaxAttempts) {
        $completedDeleteJobs = $deleteJobs | Where-Object State -eq 'Completed'
        if ($completedDeleteJobs) {
            $completedDeleteJobs | Receive-Job | Out-Null
            $completedDeleteJobs | Remove-Job
            $deleteJobs = @($deleteJobs | Where-Object State -ne 'Completed')
        }

        Write-Host "Retrying after $RetryDelaySeconds seconds with a new name. The failed resource deletion and next creation will overlap."
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

if (-not [string]::IsNullOrWhiteSpace($failedName)) {
    az signalr delete `
        --subscription $Subscription `
        --resource-group $ResourceGroup `
        --name $failedName `
        --output none
}

throw "Failed to deploy a SignalR resource after $MaxAttempts attempts."