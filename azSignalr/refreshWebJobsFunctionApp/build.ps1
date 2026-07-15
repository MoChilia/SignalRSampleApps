# Builds the sample against the locally-built WebJobs SignalR extension + local azure-signalr
# Management SDK (which has the unreleased RefreshAuthAsync / GetConnectionClaimsAsync).
#
# The flags are passed as GLOBAL properties (not AdditionalProperties) so they flow to BOTH restore
# and build of the referenced extension project. Requires a net11-capable .NET SDK (the local
# Microsoft.Azure.SignalR project multi-targets net11.0); the default SDK on this machine is preview net11.
#
# Usage:
#   .\build.ps1                 # build
#   .\build.ps1 -Target run     # func start (requires Azure Functions Core Tools + AzureSignalRConnectionString)

param(
    [ValidateSet('build', 'run', 'clean')]
    [string]$Target = 'build'
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $props = @(
        '-p:UseLocalSignalRSdk=true'
        '-p:EnableSourceControlManagerQueries=false'
        '-p:EnableSourceLink=false'
        '-p:DeterministicSourcePaths=false'
    )

    switch ($Target) {
        'clean' {
            Remove-Item -Recurse -Force bin, obj -ErrorAction SilentlyContinue
            Write-Host 'Cleaned bin/obj.'
        }
        'build' {
            dotnet build @props
        }
        'run' {
            # Build first with the required global properties, then start the host with --no-build.
            # `func start` otherwise runs its OWN `dotnet build` WITHOUT these -p flags, which rebuilds
            # the referenced azure-sdk-for-net extension (and its Azure.SdkAnalyzers SourceLink step) and
            # fails with the git 'refstorage' error. --no-build makes func reuse the output we just built.
            dotnet build @props
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
            func start --no-build
        }
    }
}
finally {
    Pop-Location
}
