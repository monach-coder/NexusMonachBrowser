# Воспроизводимая загрузка транспортного движка (MPL-2.0) в дерево проекта.
# Бинари не хранятся в git — скрипт приводит рабочую копию в состояние,
# из которого Build-Portable собирает транспортный пак поставки.
#
# Использование: .\scripts\Get-Xray.ps1 [-Version 26.3.27]
param(
    [string]$Version = "26.3.27"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "src\NexusMonach\xray"
$zip = Join-Path $env:TEMP "Xray-windows-64-$Version.zip"

New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host "Скачиваю Xray-core v$Version (windows x64)…" -ForegroundColor Cyan
$url = "https://github.com/XTLS/Xray-core/releases/download/v$Version/Xray-windows-64.zip"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

Write-Host "Проверяю SHA-256 по официальному .dgst…" -ForegroundColor Cyan
$dgst = (Invoke-WebRequest -Uri "$url.dgst" -UseBasicParsing).Content
$expected = ($dgst | Select-String -Pattern "SHA2-256=\s*([0-9a-f]{64})").Matches[0].Groups[1].Value
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    throw "SHA-256 не совпал: ожидался $expected, получен $actual"
}
Write-Host "  SHA-256 подтверждён: $actual" -ForegroundColor Green

Write-Host "Распаковываю в $target…" -ForegroundColor Cyan
$stage = Join-Path $env:TEMP "xray-stage-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Expand-Archive $zip $stage
foreach ($name in "xray.exe", "geoip.dat", "geosite.dat") {
    Copy-Item (Join-Path $stage $name) $target -Force
}
Copy-Item (Join-Path $stage "LICENSE") (Join-Path $target "LICENSE-Xray-MPL-2.0.txt") -Force
Remove-Item $stage -Recurse -Force
Remove-Item $zip -Force

Write-Host "Готово. Транспорт лежит в src\NexusMonach\xray (лицензия MPL-2.0 приложена)." -ForegroundColor Green
