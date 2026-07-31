param(
    [Parameter(Mandatory = $true)]
    [string]$InputFile,
    [string]$PrivateKey = (Join-Path (Split-Path -Parent $PSScriptRoot) ".guardian-key\report-mail-v1\crash-report-private-key.pem"),
    [string]$OutputFile
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Nexus.Guardian\Nexus.Guardian.csproj"

$inputFull = (Resolve-Path $InputFile).Path
$keyFull = (Resolve-Path $PrivateKey).Path
if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $OutputFile = [IO.Path]::ChangeExtension($inputFull, ".decrypted.json")
}

dotnet run --project $project --configuration Release -- `
    --decrypt-report $inputFull $keyFull $OutputFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Decrypted report: $OutputFile" -ForegroundColor Green
