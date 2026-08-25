param(
    [string]$Dist = "dist/NexusMonach-Portable",
    # Ключ подписи = ключ самого дистрибутива: локальная сборка подписана
    # девелоперским, официальная (CI, GUARDIAN_OFFICIAL) — official-v1.
    # Установщику для проверки кладём публичную половину того же ключа.
    [string]$PrivateKeyPath = ".guardian-key/integrity-private-key.pem",
    [string]$Output = "dist/release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$root = (Resolve-Path $Dist).Path
$guardian = Join-Path $root "NexusMonach.exe"
if (-not (Test-Path $guardian)) { throw "Guardian launcher not found: $guardian" }

# The manifest is written into the build: core zip is looked up next to it.
$invokeGuardian = Join-Path $PSScriptRoot "Invoke-Guardian.ps1"
$arguments = @("--create-release-manifest", $root)
if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath) -and (Test-Path $PrivateKeyPath)) {
    $arguments += "--private-key"
    $arguments += (Resolve-Path $PrivateKeyPath).Path
}
& $invokeGuardian -Guardian $guardian -GuardianArguments $arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Publishing stage: manifest + signature + all delivery packs in one
# directory, uploaded to a GitHub Release without renames.
$out = New-Item -ItemType Directory -Force -Path $Output
Copy-Item (Join-Path $root "release-manifest.json") $out.FullName -Force
$sig = Join-Path $root "release-manifest.json.sig"
if (Test-Path $sig) { Copy-Item $sig $out.FullName -Force }
Get-ChildItem "dist" -Filter "nexus-*.zip" | Copy-Item -Destination $out.FullName -Force

# Установщик проверяет подпись манифеста тем же ключом, которым подписан
# дистрибутив: кладём публичную половину рядом с проектом установщика.
$distPublic = Join-Path $root "dist\NexusMonach-Portable\integrity-public-key.pem"
if (Test-Path $distPublic) {
    Copy-Item $distPublic (Join-Path $root "src\Nexus.Setup\integrity-public-key.pem") -Force
    Write-Host "Setup trust key refreshed from dist" -ForegroundColor Cyan
}

Write-Host "Release staging ready: $Output" -ForegroundColor Green
Get-ChildItem $out.FullName | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
