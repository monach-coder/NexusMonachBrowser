param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ".guardian-key")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Nexus.Guardian\Nexus.Guardian.csproj"
$security = Join-Path $root "security"

dotnet run --project $project --configuration Release -- --generate-key $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force $security | Out-Null
Copy-Item (Join-Path $OutputDirectory "integrity-public-key.pem") `
    (Join-Path $security "integrity-public-key.pem") -Force

Write-Host "Nexus Guardian key pair created." -ForegroundColor Green
Write-Host "Public key (commit this file): $security\integrity-public-key.pem"
Write-Host "Private key (NEVER commit it): $OutputDirectory\integrity-private-key.pem"
Write-Host "Add the private PEM to GitHub Secret NEXUS_INTEGRITY_PRIVATE_KEY_BASE64 as Base64 UTF-8."
