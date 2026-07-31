param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path
$errors = [System.Collections.Generic.List[string]]::new()

function Read-Source([string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path $path)) {
        $errors.Add("Missing secure network source: $RelativePath")
        return ''
    }
    Get-Content $path -Raw
}

function Require-Text([string]$Source, [string]$Needle, [string]$Message) {
    if ($Source.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        $errors.Add($Message)
    }
}

$settings = Read-Source 'src/NexusMonach/Models/BrowserSettings.cs'
Require-Text $settings 'HttpsFirstEnabled { get; set; } = true' 'HTTPS-first must default to enabled.'
Require-Text $settings 'SecureDnsMode { get; set; } = SecureDnsMode.Strict' 'Strict DoH must be the default.'
if ($settings.IndexOf('FamilyProtectionEnabled', [StringComparison]::Ordinal) -ge 0) {
    $errors.Add('A blanket family-content switch must not be part of the secure network boundary.')
}

$network = Read-Source 'src/NexusMonach/Services/SecureNetworkConfigurationService.cs'
Require-Text $network '--dns-over-https-mode=' 'Secure DNS mode must be applied to WebView2.'
Require-Text $network 'https://cloudflare-dns.com/dns-query' 'Cloudflare DoH must be pinned in code.'
Require-Text $network 'https://dns.quad9.net/dns-query' 'Quad9 DoH must be pinned in code.'

$resolver = Read-Source 'src/NexusMonach/Services/SecureDnsResolver.cs'
Require-Text $resolver 'application/dns-message' 'Crawler DNS must use the DoH wire format.'
Require-Text $resolver 'ConnectBootstrapAsync' 'Crawler DoH must use pinned bootstrap addresses.'
Require-Text $resolver 'SecureDnsMode.Automatic' 'Only automatic mode may use the system DNS fallback.'
Require-Text $resolver 'CacheEntry' 'DNS cache must remain process-local.'

$guard = Read-Source 'src/NexusMonach/Services/NexusSearchNetworkGuard.cs'
Require-Text $guard 'SecureDnsResolver.ResolveAsync' 'Crawler destination validation must use encrypted DNS.'
Require-Text $guard 'TryNormalizePublicHttpsUri' 'Crawler links must be normalized to HTTPS.'
Require-Text $guard 'ConnectPublicAsync' 'Crawler must connect to a validated public address.'

$tab = Read-Source 'src/NexusMonach/Models/BrowserTab.cs'
Require-Text $tab 'HandleHttpsFirstNavigation' 'Top-level navigation must enforce HTTPS-first.'
Require-Text $tab 'AskToOpenHttpOnce' 'Plain HTTP must require a per-navigation confirmation.'

$search = Read-Source 'src/NexusMonach/Services/NexusSearchService.cs'
Require-Text $search 'TryGetSecureCrawlerUri' 'Crawler discovery and enrichment must use HTTPS normalization.'
if ($search.IndexOf('safesearch=strict', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $search.IndexOf('safe=active', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $search.IndexOf('family=yes', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $search.IndexOf('adlt=strict', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    $errors.Add('Forced search-engine content filtering is forbidden in the network security layer.')
}

$runtimeSources = Get-ChildItem (Join-Path $Root 'src/NexusMonach') -Recurse -File -Include *.cs,*.xaml |
    ForEach-Object { Get-Content $_.FullName -Raw }
$allRuntime = $runtimeSources -join "`n"
foreach ($forbidden in @(
    '--ignore-certificate-errors',
    'DangerousAcceptAnyServerCertificateValidator',
    'MullvadFamilyTemplate',
    'FamilySafetyService')) {
    if ($allRuntime.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $errors.Add("Forbidden network-boundary token: $forbidden")
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    throw ('Secure network boundary check failed with {0} finding(s).' -f $errors.Count)
}

Write-Host 'Secure network boundary verified: strict DoH, HTTPS-first, crawler rebinding protection and no content filter.' -ForegroundColor Green
