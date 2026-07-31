param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ".guardian-key\report-mail-v1")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Nexus.Guardian\Nexus.Guardian.csproj"
$security = Join-Path $root "security"
$privateKey = Join-Path $OutputDirectory "crash-report-private-key.pem"
$publicKey = Join-Path $OutputDirectory "crash-report-public-key.pem"

if ((Test-Path $privateKey) -or (Test-Path $publicKey)) {
    throw "Crash-report encryption key already exists: $OutputDirectory. Guardian will not overwrite it."
}

dotnet run --project $project --configuration Release -- --generate-report-key $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force $security | Out-Null
Copy-Item $publicKey `
    (Join-Path $security "crash-report-public-key.pem") -Force

Write-Host "Nexus Guardian report-encryption key pair created." -ForegroundColor Green
Write-Host "Public key (commit this file): $security\crash-report-public-key.pem"
Write-Host "Private key (NEVER commit it): $OutputDirectory\crash-report-private-key.pem"
Write-Host "Back up the private key offline. It is not a signing key and must not be uploaded to GitHub Actions."
