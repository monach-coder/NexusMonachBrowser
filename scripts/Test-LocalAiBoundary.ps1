param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path
$errors = [System.Collections.Generic.List[string]]::new()

function Read-Source([string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path $path)) {
        $errors.Add("Missing security boundary source: $RelativePath")
        return ''
    }
    return Get-Content $path -Raw
}

function Require-Text([string]$Source, [string]$Needle, [string]$Message) {
    if ($Source.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) { $errors.Add($Message) }
}

function Reject-Text([string]$Source, [string]$Needle, [string]$Message) {
    if ($Source.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0) { $errors.Add($Message) }
}

$transport = Read-Source 'src/NexusMonach/Services/LocalAiLoopbackTransport.cs'
Require-Text $transport 'UseProxy = false' 'Local AI transport must not use a proxy.'
Require-Text $transport 'AllowAutoRedirect = false' 'Local AI transport must not follow redirects.'
Require-Text $transport 'ConnectCallback = ConnectAsync' 'Local AI transport must pin its socket connection.'
Require-Text $transport 'IPAddress.TryParse' 'Local AI transport must require an IP literal.'
Require-Text $transport 'IPAddress.IsLoopback' 'Local AI transport must reject non-loopback addresses.'

$llama = Read-Source 'src/NexusMonach/Services/LocalTextModelServer.cs'
Require-Text $llama 'RandomNumberGenerator.GetBytes(32)' 'Llama session key must contain 256 random bits.'
Require-Text $llama 'start.Environment["LLAMA_API_KEY"]' 'Llama key must be passed through the child environment.'
Require-Text $llama 'new AuthenticationHeaderValue("Bearer", apiKey)' 'Llama requests must authenticate.'
Require-Text $llama 'VerifyAuthenticationAsync' 'Llama startup must verify that authentication is active.'
Reject-Text $llama '"--api-key"' 'Llama key must not be exposed in process arguments.'

$whisper = Read-Source 'src/NexusMonach/Services/WhisperService.cs'
Require-Text $whisper 'RandomNumberGenerator.GetBytes(24)' 'Whisper route must use a random session capability.'
Require-Text $whisper '"--inference-path", inferencePath' 'Whisper must receive the random inference route.'
Reject-Text $whisper '"--inference-path", "/inference"' 'Whisper must not expose the fixed inference route.'

$tab = Read-Source 'src/NexusMonach/Models/BrowserTab.cs'
Require-Text $tab 'source.Scheme != Uri.UriSchemeHttps' 'Internal WebView messages must require HTTPS.'
Require-Text $tab '!source.IsDefaultPort' 'Internal WebView messages must reject a non-default port.'
Require-Text $tab 'IsAllowedInternalMessage(page, type)' 'Internal pages must have per-page message allowlists.'

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    throw "Local AI security boundary check failed with $($errors.Count) finding(s)."
}

Write-Host 'Local AI boundary verified: loopback pinning, session authentication and WebView origin allowlists are present.' -ForegroundColor Green
