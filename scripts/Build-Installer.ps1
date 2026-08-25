param(
    [string]$Output = "dist"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet publish src/Nexus.Setup/Nexus.Setup.csproj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $published = "src/Nexus.Setup/bin/Release/net8.0-windows/win-x64/publish/NexusMonach-Setup.exe"
    if (-not (Test-Path $published)) { throw "Опубликованный установщик не найден: $published" }
    Copy-Item $published $Output -Force
    $size = [math]::Round((Get-Item (Join-Path $Output "NexusMonach-Setup.exe")).Length / 1MB, 1)
    Write-Host "Установщик готов: $Output\NexusMonach-Setup.exe ($size МБ)" -ForegroundColor Green
}
finally { Pop-Location }
