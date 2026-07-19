param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [string]$PrivateKeyPath
)

$ErrorActionPreference = "Stop"
$guardian = Join-Path $Directory "NexusMonach.exe"
if (-not (Test-Path $guardian)) { throw "Nexus Guardian launcher not found: $guardian" }

$arguments = @("--create-manifest", (Resolve-Path $Directory).Path)
if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $arguments += "--private-key"
    $arguments += (Resolve-Path $PrivateKeyPath).Path
}

$invokeGuardian = Join-Path $PSScriptRoot "Invoke-Guardian.ps1"
& $invokeGuardian -Guardian $guardian -GuardianArguments $arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Integrity manifest created: $Directory" -ForegroundColor Green
