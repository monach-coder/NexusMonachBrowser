param(
    [switch]$OfficialGuardianBuild,
    [string]$GuardianReportEndpoint = $env:NEXUS_GUARDIAN_REPORT_ENDPOINT,
    [string]$GuardianReportIngestKey = $env:NEXUS_GUARDIAN_REPORT_INGEST_KEY,
    [ValidateSet('ask', 'automatic')]
    [string]$GuardianReportMode = $(if ($env:NEXUS_GUARDIAN_REPORT_MODE) { $env:NEXUS_GUARDIAN_REPORT_MODE } else { 'ask' })
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\NexusMonach\NexusMonach.csproj"
$guardianProject = Join-Path $root "src\Nexus.Guardian\Nexus.Guardian.csproj"
$invokeGuardian = Join-Path $PSScriptRoot "Invoke-Guardian.ps1"
$dist = Join-Path $root "dist"
$publish = Join-Path $dist "NexusMonach-Portable"
$archive = Join-Path $dist "NexusMonach-Portable-win-x64.zip"
$ai = Join-Path $root "src\NexusMonach\AI"

& (Join-Path $PSScriptRoot "Test-LicenseBoundary.ps1") -Root $root
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot "Test-SecureNetworkBoundary.ps1") -Root $root
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK was not found." -ForegroundColor Red
    Write-Host "Install it from: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$requiredAi = @(
    (Join-Path $ai "llama\llama-cli.exe"),
    (Join-Path $ai "llama\llama-server.exe"),
    (Join-Path $ai "llama\llama-mtmd-cli.exe"),
    (Join-Path $ai "node\node.exe"),
    (Join-Path $ai "adapters\semantic.mjs"),
    (Join-Path $ai "adapters\translate.mjs"),
    (Join-Path $ai "models\whisper\ggml-base-q5_1.bin"),
    (Join-Path $ai "models\smolvlm-500m\SmolVLM-500M-Instruct-Q8_0.gguf"),
    (Join-Path $ai "models\smolvlm-500m\mmproj-SmolVLM-500M-Instruct-Q8_0.gguf"),
    (Join-Path $ai "models\multilingual-e5-small\onnx\model.onnx"),
    (Join-Path $ai "models\translation\mul-en\onnx\encoder_model_quantized.onnx"),
    (Join-Path $ai "models\translation\mul-en\onnx\decoder_model_merged_quantized.onnx"),
    (Join-Path $ai "models\translation\ko-en\onnx\encoder_model_quantized.onnx"),
    (Join-Path $ai "models\translation\ko-en\onnx\decoder_model_merged_quantized.onnx"),
    (Join-Path $ai "models\translation\en-ru\onnx\encoder_model_quantized.onnx"),
    (Join-Path $ai "models\translation\en-ru\onnx\decoder_model_merged_quantized.onnx")
)
$textModels = @(Get-ChildItem (Join-Path $ai "models\qwen3-0.6b") -Filter *.gguf -ErrorAction SilentlyContinue)
$missingAi = @($requiredAi | Where-Object { -not (Test-Path $_) })
if ($textModels.Count -eq 0) { $missingAi += (Join-Path $ai "models\qwen3-0.6b\*.gguf") }
if (@(Get-ChildItem (Join-Path $ai "whisper") -Filter whisper-cli.exe -Recurse -ErrorAction SilentlyContinue).Count -eq 0) {
    $missingAi += (Join-Path $ai "whisper\**\whisper-cli.exe")
}
if (@(Get-ChildItem (Join-Path $ai "whisper") -Filter whisper-server.exe -Recurse -ErrorAction SilentlyContinue).Count -eq 0) {
    $missingAi += (Join-Path $ai "whisper\**\whisper-server.exe")
}
if ($missingAi.Count -gt 0) {
    Write-Host "WARNING: source build does not contain the Full Offline AI payload:" -ForegroundColor Yellow
    $missingAi | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host "The browser will build, but AI functions require the official Full Offline release." -ForegroundColor Yellow
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $project
if ($LASTEXITCODE -ne 0) {
    Write-Host "Package restore failed." -ForegroundColor Red
    exit $LASTEXITCODE
}
dotnet restore $guardianProject
if ($LASTEXITCODE -ne 0) {
    Write-Host "Guardian package restore failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Building the portable Windows x64 version..." -ForegroundColor Cyan
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publish `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -warnaserror
if ($LASTEXITCODE -ne 0) {
    Write-Host "Compilation failed. No portable archive was created." -ForegroundColor Red
    exit $LASTEXITCODE
}

$executable = Join-Path $publish "NexusMonach.exe"
if (-not (Test-Path (Join-Path $publish "NexusMonach.Browser.exe"))) {
    Write-Host "Compilation finished without NexusMonach.Browser.exe. Packaging was cancelled." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing Nexus Guardian..." -ForegroundColor Cyan
$guardianConstants = if ($OfficialGuardianBuild) { "GUARDIAN_OFFICIAL" } else { "" }
dotnet publish $guardianProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publish `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    "-p:DefineConstants=$guardianConstants" `
    -warnaserror
if ($LASTEXITCODE -ne 0) {
    Write-Host "Nexus Guardian compilation failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

$executable = Join-Path $publish "NexusMonach.exe"
if (-not (Test-Path $executable)) {
    Write-Host "Compilation finished without the Nexus Guardian launcher." -ForegroundColor Red
    exit 1
}

$manifestPrivateKey = $null
if ($OfficialGuardianBuild) {
    $publicKey = Join-Path $root "security\integrity-public-key.pem"
    if (Test-Path $publicKey) { Copy-Item $publicKey $publish -Force }
} else {
    # Exercise the same signed-manifest path in a local build. This key remains
    # outside the portable archive and is ignored by Git.
    $localKeyDirectory = Join-Path $root ".guardian-key"
    $localPrivateKey = Join-Path $localKeyDirectory "integrity-private-key.pem"
    $localPublicKey = Join-Path $localKeyDirectory "integrity-public-key.pem"
    if (-not (Test-Path $localPrivateKey) -or -not (Test-Path $localPublicKey)) {
        Write-Host "Creating a local Nexus Guardian development key..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $localKeyDirectory | Out-Null
        Remove-Item $localPrivateKey,$localPublicKey -Force -ErrorAction SilentlyContinue
        & $invokeGuardian -Guardian $executable -GuardianArguments @("--generate-key", $localKeyDirectory)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localPrivateKey) -or -not (Test-Path $localPublicKey)) {
            Write-Host "Nexus Guardian could not create the local integrity key." -ForegroundColor Red
            exit 1
        }
    }
    Copy-Item $localPublicKey (Join-Path $publish "integrity-public-key.pem") -Force
    $manifestPrivateKey = $localPrivateKey
}

New-Item -ItemType File -Force -Path (Join-Path $publish "portable.flag") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($GuardianReportEndpoint)) {
    [Uri]$reportUri = $null
    if (-not [Uri]::TryCreate($GuardianReportEndpoint, [UriKind]::Absolute, [ref]$reportUri) -or
        $reportUri.Scheme -ne 'https') {
        throw 'GuardianReportEndpoint must be an absolute HTTPS URL.'
    }
    @{
        endpoint = $reportUri.AbsoluteUri
        ingestKey = $GuardianReportIngestKey
        mode = $GuardianReportMode
    } | ConvertTo-Json | Set-Content (Join-Path $publish 'guardian-reporting.json') -Encoding utf8
}
Copy-Item (Join-Path $root "README.md") $publish
Copy-Item (Join-Path $root "CHANGELOG.md") $publish
Copy-Item (Join-Path $root "PRIVACY.md") $publish
Copy-Item (Join-Path $root "SECURITY.md") $publish
Copy-Item (Join-Path $root "LICENSE") $publish
Copy-Item (Join-Path $root "NOTICE.md") $publish
Copy-Item (Join-Path $root "TRADEMARKS.md") $publish
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") $publish

# Local packages use a per-workstation development signature. The official
# workflow creates an unsigned placeholder here and signs the final payload only
# after Authenticode has finished.
if ($OfficialGuardianBuild) {
    & $invokeGuardian -Guardian $executable -GuardianArguments @("--create-manifest", $publish)
} else {
    & $invokeGuardian -Guardian $executable -GuardianArguments @(
        "--create-manifest", $publish, "--private-key", $manifestPrivateKey)
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Nexus Guardian could not create the integrity manifest." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not $OfficialGuardianBuild) {
    Write-Host "Verifying the signed local payload..." -ForegroundColor Cyan
    & $invokeGuardian -Guardian $executable -GuardianArguments @(
        "--verify-only", $publish, "--full-integrity-check")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "The local Guardian manifest did not pass full verification." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $archive -CompressionLevel Optimal

Write-Host "" 
Write-Host "Done:" -ForegroundColor Green
Write-Host $archive
