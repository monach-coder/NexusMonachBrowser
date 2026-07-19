param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [Parameter(Mandatory = $true)][string]$CertificatePath,
    [Parameter(Mandatory = $true)][string]$CertificatePassword,
    [string]$TimestampUrl = ''
)

$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path $Directory).Path
$CertificatePath = (Resolve-Path $CertificatePath).Path

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $signTool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem $kits -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName -First 1
}
if (-not $signTool) { throw 'signtool.exe was not found. Install Windows SDK build tools.' }

$targets = @(
    (Join-Path $Directory 'NexusMonach.exe'),
    (Join-Path $Directory 'NexusMonach.Browser.exe'),
    (Join-Path $Directory 'Nexus.Intelligence.Fabric.dll'),
    (Join-Path $Directory 'Nexus.Intelligence.Contracts.dll')
) | Where-Object { Test-Path $_ }
if ($targets.Count -eq 0) { throw "No signable Nexus binaries found in $Directory" }

foreach ($target in $targets) {
    $arguments = @('sign','/fd','SHA256','/f',$CertificatePath,'/p',$CertificatePassword)
    if ($TimestampUrl) { $arguments += @('/tr',$TimestampUrl,'/td','SHA256') }
    $arguments += $target
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $target" }
    & $signTool verify /pa /v $target
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $target" }
}

Write-Host "Authenticode signatures verified for $($targets.Count) file(s)." -ForegroundColor Green
