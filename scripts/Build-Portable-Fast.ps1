# Быстрая тестовая сборка Nexus Monach в отдельную папку dist-fast.
# Отличия от Build-Portable.ps1: отдельный вывод, без zip-архива, AI-модели
# подтягиваются из установленной сборки D:\NexusMonach ДО создания манифеста.
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH

# В этой сессии отсутствуют стандартные переменные среды — без них NuGet падает
# с "Value cannot be null (path1)" в XPlatMachineWideSetting.
if ([string]::IsNullOrEmpty($env:USERPROFILE)) {
    $env:USERPROFILE = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
}
if ([string]::IsNullOrEmpty($env:APPDATA)) {
    $env:APPDATA = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
}
if ([string]::IsNullOrEmpty($env:LOCALAPPDATA)) {
    $env:LOCALAPPDATA = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
}
if ([string]::IsNullOrEmpty($env:ProgramData)) { $env:ProgramData = "C:\ProgramData" }
if ([string]::IsNullOrEmpty($env:ALLUSERSPROFILE)) { $env:ALLUSERSPROFILE = $env:ProgramData }
if ([string]::IsNullOrEmpty($env:PUBLIC)) { $env:PUBLIC = "C:\Users\Public" }
if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable("PROGRAMFILES"))) {
    [Environment]::SetEnvironmentVariable("PROGRAMFILES", "C:\Program Files")
}
if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable("PROGRAMFILES(X86)"))) {
    [Environment]::SetEnvironmentVariable("PROGRAMFILES(X86)", "C:\Program Files (x86)")
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\NexusMonach\NexusMonach.csproj"
$guardianProject = Join-Path $root "src\Nexus.Guardian\Nexus.Guardian.csproj"
$invokeGuardian = Join-Path $PSScriptRoot "Invoke-Guardian.ps1"
$dist = Join-Path $root "dist-fast"
$publish = Join-Path $dist "NexusMonach-Portable"
$aiSource = "D:\NexusMonach\AI"

& (Join-Path $PSScriptRoot "Test-LicenseBoundary.ps1") -Root $root
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot "Test-SecureNetworkBoundary.ps1") -Root $root
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet restore $guardianProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building the portable Windows x64 version (ReadyToRun)..." -ForegroundColor Cyan
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publish `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -warnaserror
if ($LASTEXITCODE -ne 0) { Write-Host "Compilation failed." -ForegroundColor Red; exit $LASTEXITCODE }
if (-not (Test-Path (Join-Path $publish "NexusMonach.Browser.exe"))) {
    Write-Host "Compilation finished without NexusMonach.Browser.exe." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing Nexus Guardian..." -ForegroundColor Cyan
dotnet publish $guardianProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publish `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    "-p:DefineConstants=" `
    -warnaserror
if ($LASTEXITCODE -ne 0) { Write-Host "Nexus Guardian compilation failed." -ForegroundColor Red; exit $LASTEXITCODE }

$executable = Join-Path $publish "NexusMonach.exe"
if (-not (Test-Path $executable)) {
    Write-Host "Compilation finished without the Nexus Guardian launcher." -ForegroundColor Red
    exit 1
}

# AI-модели и внешние зависимости из установленной сборки — ДО подписи манифеста.
$aiDest = Join-Path $publish "AI"
foreach ($item in @("models", "node_modules", "ffmpeg")) {
    $s = Join-Path $aiSource $item
    $d = Join-Path $aiDest $item
    if ((Test-Path $s) -and -not (Test-Path $d)) {
        Write-Host "AI payload: $item" -ForegroundColor Cyan
        robocopy $s $d /E /MT:16 /NFL /NDL /NJH /NJS | Out-Null
    }
}

$localKeyDirectory = Join-Path $root ".guardian-key"
$localPrivateKey = Join-Path $localKeyDirectory "integrity-private-key.pem"
$localPublicKey = Join-Path $localKeyDirectory "integrity-public-key.pem"
if (-not (Test-Path $localPrivateKey) -or -not (Test-Path $localPublicKey)) {
    Write-Host "Creating a local Nexus Guardian development key..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $localKeyDirectory | Out-Null
    Remove-Item $localPrivateKey,$localPublicKey -Force -ErrorAction SilentlyContinue
    & $invokeGuardian -Guardian $executable -GuardianArguments @("--generate-key", $localKeyDirectory)
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
Copy-Item $localPublicKey (Join-Path $publish "integrity-public-key.pem") -Force

New-Item -ItemType File -Force -Path (Join-Path $publish "portable.flag") | Out-Null
foreach ($doc in @("README.md","CHANGELOG.md","PRIVACY.md","SECURITY.md","LICENSE","NOTICE.md","TRADEMARKS.md","THIRD_PARTY_NOTICES.md")) {
    Copy-Item (Join-Path $root $doc) $publish
}

Write-Host "Creating the signed integrity manifest..." -ForegroundColor Cyan
& $invokeGuardian -Guardian $executable -GuardianArguments @(
    "--create-manifest", $publish, "--private-key", $localPrivateKey)
if ($LASTEXITCODE -ne 0) { Write-Host "Manifest creation failed." -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host "Verifying the signed local payload..." -ForegroundColor Cyan
& $invokeGuardian -Guardian $executable -GuardianArguments @(
    "--verify-only", $publish, "--full-integrity-check")
if ($LASTEXITCODE -ne 0) { Write-Host "Manifest verification failed." -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host $publish
