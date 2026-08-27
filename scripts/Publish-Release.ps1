param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$Prerelease,
    [string]$PrivateKeyPath = ".guardian-key/official-v1/integrity-private-key.pem",
    [string]$Notes = ""
)

# Одна команда — один релиз: версия -> официальная сборка -> подписанные
# пакеты -> установщик -> GitHub Release -> проверка живых ссылок -> пуш.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

if (-not (Test-Path $PrivateKeyPath)) {
    throw "Integrity private key not found: $PrivateKeyPath (official releases are cut from a trusted machine)"
}

# 0. Чистое состояние: main, без несобранных изменений, без работающего браузера.
$branch = git branch --show-current
if ($branch -ne "main") { throw "Releases are cut from main only (current: $branch)" }
if (git status --porcelain) { throw "Working tree is not clean - commit first" }
$running = Get-Process NexusMonach,NexusMonach.Browser,NexusMonach-Setup,nexus-silero-worker,whisper-server,piper,llama-server -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running browser processes..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep 2
}

# 1. Версия в едином источнике правды.
$props = "Directory.Build.props"
$content = Get-Content $props -Raw
$updated = $content -replace '<NexusProductVersion>[^<]*</NexusProductVersion>',
    "<NexusProductVersion>$Version</NexusProductVersion>"
if ($updated -eq $content -and $content -notmatch [regex]::Escape($Version)) {
    throw "Failed to bump NexusProductVersion in $props"
}
Set-Content $props $updated -Encoding UTF8 -NoNewline
git add $props
git commit -m "Release $Version" --quiet
Write-Host "Version bumped to $Version" -ForegroundColor Cyan

# 2. Официальная портативная сборка с подписью манифеста.
# SkipArchive: полный zip не входит в релиз (пакеты поставки собирает
# New-ReleaseManifest), а его сжатие съедает гигабайты на C:.
& (Join-Path $PSScriptRoot "Build-Portable.ps1") -OfficialGuardianBuild -PrivateKeyPath $PrivateKeyPath -SkipArchive
if ($LASTEXITCODE -ne 0) { throw "Build-Portable failed" }

# 3. Подписанные пакеты сетевой поставки + ключ доверия установщика.
& (Join-Path $PSScriptRoot "New-ReleaseManifest.ps1") -PrivateKeyPath $PrivateKeyPath
if ($LASTEXITCODE -ne 0) { throw "New-ReleaseManifest failed" }

# 4. Установщик.
& (Join-Path $PSScriptRoot "Build-Installer.ps1")
if ($LASTEXITCODE -ne 0) { throw "Build-Installer failed" }

# 5. Публикация релиза.
$tag = "v$Version"
$assets = @(
    "dist\NexusMonach-Setup.exe",
    "dist\release\release-manifest.json",
    "dist\release\release-manifest.json.sig",
    "dist\release\nexus-core.zip",
    "dist\release\nexus-ai-runtime.zip",
    "dist\release\nexus-ai-models.zip",
    "dist\release\nexus-ai-vlm.zip"
) | Where-Object { Test-Path $_ }
if ($assets.Count -lt 4) { throw "Release assets are missing (found $($assets.Count))" }

if (-not $Notes) {
    $Notes = "Nexus Monach $Version.`n`nУстановка - NexusMonach-Setup.exe (~68 MB): ядро за секунды, нейросети подтягиваются по сети. Существующие установки обновятся автоматически при следующем запуске браузера."
}
gh release create $tag @assets --repo monach-coder/NexusMonachBrowser `
    --title "Nexus Monach $Version" --notes $Notes @($(if ($Prerelease) { "--prerelease" }))
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

# 6. Живая проверка точек раздачи.
Start-Sleep 5
$latest = "https://github.com/monach-coder/NexusMonachBrowser/releases/latest/download"
foreach ($probe in @("release-manifest.json", "NexusMonach-Setup.exe")) {
    $code = try { (Invoke-WebRequest -Uri "$latest/$probe" -Method Head -MaximumRedirection 5 -UseBasicParsing).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
    if ($code -ne 200) { throw "Release check failed for $probe (HTTP $code)" }
}
Write-Host "Release URLs verified" -ForegroundColor Green

# 7. Пуш версии в main.
git push origin main --quiet
Write-Host ""
Write-Host "RELEASED: https://github.com/monach-coder/NexusMonachBrowser/releases/tag/$tag" -ForegroundColor Green
Write-Host "Users on auto-update will pick it up on their next browser launch."
