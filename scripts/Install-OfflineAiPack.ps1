param(
    [Parameter(Mandatory = $true)]
    [string]$Archive
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root "src\NexusMonach\AI"
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("NexusAiPack-" + [guid]::NewGuid().ToString("N"))

try {
    $archivePath = (Resolve-Path $Archive).Path
    New-Item -ItemType Directory -Force -Path $temporary | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $temporary

    $packRoot = if (Test-Path (Join-Path $temporary "AI")) { Join-Path $temporary "AI" } else { $temporary }
    $checksumFile = Join-Path $packRoot "checksums.sha256"
    if (-not (Test-Path $checksumFile)) { throw "AI pack has no checksums.sha256." }

    $required = @(
        "llama\llama-cli.exe",
        "llama\llama-server.exe",
        "llama\llama-mtmd-cli.exe",
        "node\node.exe",
        "adapters\semantic.mjs",
        "models\whisper\ggml-base-q5_1.bin",
        "models\smolvlm-500m\SmolVLM-500M-Instruct-Q8_0.gguf",
        "models\smolvlm-500m\mmproj-SmolVLM-500M-Instruct-Q8_0.gguf",
        "models\multilingual-e5-small\onnx\model.onnx"
    )
    foreach ($relative in $required) {
        if (-not (Test-Path (Join-Path $packRoot $relative))) { throw "AI pack is missing $relative" }
    }
    if (@(Get-ChildItem (Join-Path $packRoot "whisper") -Filter whisper-cli.exe -Recurse -ErrorAction SilentlyContinue).Count -eq 0) {
        throw "AI pack has no whisper-cli.exe."
    }
    if (@(Get-ChildItem (Join-Path $packRoot "models\qwen3-0.6b") -Filter *.gguf -ErrorAction SilentlyContinue).Count -eq 0) {
        throw "AI pack has no Qwen3 0.6B GGUF model."
    }

    $expected = @{}
    foreach ($line in Get-Content $checksumFile) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            $relative = $matches[2].Replace('/', '\')
            if ($relative.Contains('..')) { throw "Unsafe checksum path: $relative" }
            $expected[$relative] = $matches[1].ToUpperInvariant()
        }
    }
    if ($expected.Count -eq 0) { throw "checksums.sha256 is empty or invalid." }
    foreach ($relative in $expected.Keys) {
        $file = Join-Path $packRoot $relative
        if (-not (Test-Path $file -PathType Leaf)) { throw "Checksum references missing file: $relative" }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
        if ($actual -ne $expected[$relative]) { throw "SHA-256 mismatch: $relative" }
    }

    Get-ChildItem $destination -Force | Where-Object { $_.Name -notin @('README.txt', 'model-pack.json') } |
        Remove-Item -Recurse -Force
    Copy-Item (Join-Path $packRoot "*") $destination -Recurse -Force
    Write-Host "Verified Offline AI pack installed into $destination" -ForegroundColor Green
}
finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
}
