param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path
$errors = [System.Collections.Generic.List[string]]::new()

$required = @(
    'LICENSE',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'TRADEMARKS.md',
    'NOTICE.md',
    'THIRD_PARTY_NOTICES.md',
    'src/Nexus.Intelligence.Contracts/Nexus.Intelligence.Contracts.csproj',
    'src/Nexus.Intelligence.Fabric/Nexus.Intelligence.Fabric.csproj',
    'src/Nexus.Intelligence.Fabric/NexusIntelligenceFabric.cs',
    'src/Nexus.Guardian/Nexus.Guardian.csproj',
    'src/Nexus.Guardian.Relay/Nexus.Guardian.Relay.csproj',
    'docs/Nexus-Guardian.md',
    'security/crash-report-public-key.pem'
)
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $Root $relative))) { $errors.Add("Missing required project file: $relative") }
}

$tracked = @()
if (Test-Path (Join-Path $Root '.git')) {
    $tracked = @(git -C $Root ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
} else {
    $tracked = @(Get-ChildItem $Root -File -Recurse | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1).Replace('\','/')
    })
}

$forbiddenPaths = @(
    '(^|/)(bin|obj|dist)/',
    '\.(pfx|p12|snk|key)$',
    '(^|/)crash-report-private-key\.pem$',
    '^src/NexusMonach/AI/.*\.(gguf|bin|onnx|exe|dll)$'
)
foreach ($file in $tracked) {
    foreach ($pattern in $forbiddenPaths) {
        if ($file -match $pattern) { $errors.Add("Generated, secret or large binary file must not be tracked: $file") }
    }
}

$privateKeys = Get-ChildItem $Root -File -Recurse -Include *.cs,*.json,*.yml,*.yaml,*.ps1,*.md |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|\.git)[\\/]' } |
    Select-String -Pattern '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----' -SimpleMatch:$false
if ($privateKeys) {
    $privateKeys | ForEach-Object { $errors.Add("Private key material detected: $($_.Path):$($_.LineNumber)") }
}

$solution = Get-Content (Join-Path $Root 'NexusMonach.sln') -Raw
if ($solution -notmatch 'Nexus\.Intelligence\.Fabric') {
    $errors.Add('Open Fabric project is not included in NexusMonach.sln.')
}
if ($solution -notmatch 'Nexus\.Guardian') {
    $errors.Add('Nexus Guardian project is not included in NexusMonach.sln.')
}
if ($solution -notmatch 'Nexus\.Guardian\.Relay') {
    $errors.Add('Nexus Guardian Relay project is not included in NexusMonach.sln.')
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    throw "Repository hygiene check failed with $($errors.Count) finding(s)."
}

Write-Host 'Repository verified: open Fabric source is present; no signing keys or generated AI payloads are tracked.' -ForegroundColor Green
