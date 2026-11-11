param(
    [switch]$SkipDriverIntegration
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceAfisRoot = $PSScriptRoot
$driverServices = Join-Path $sourceAfisRoot '..\DriverControlID\DriverControlID.Services\DriverControlID.Services.csproj'

dotnet test (Join-Path $sourceAfisRoot 'SourceAFIS.sln') `
    --configuration Release `
    --logger 'console;verbosity=normal'

if ($LASTEXITCODE -ne 0) {
    throw "SourceAFIS test suite failed with exit code $LASTEXITCODE."
}

if (-not $SkipDriverIntegration) {
    dotnet build $driverServices `
        --configuration Release `
        --property:Platform=x64 `
        --property:UseLocalSourceAfis=true

    if ($LASTEXITCODE -ne 0) {
        throw "DriverControlID integration build failed with exit code $LASTEXITCODE."
    }
}

Write-Host 'Experimental verification completed successfully.' -ForegroundColor Green
