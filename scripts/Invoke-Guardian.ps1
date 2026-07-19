param(
    [Parameter(Mandatory = $true)][string]$Guardian,
    [Parameter(Mandatory = $true)][string[]]$GuardianArguments
)

$ErrorActionPreference = 'Stop'
$resolvedGuardian = (Resolve-Path $Guardian).Path

# Windows PowerShell may return immediately when a GUI-subsystem executable is
# invoked with the call operator. Start-Process -Wait gives both Windows
# PowerShell 5.1 and PowerShell 7 the same deterministic behaviour.
$argumentLine = ($GuardianArguments | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
}) -join ' '

$process = Start-Process -FilePath $resolvedGuardian `
    -ArgumentList $argumentLine `
    -WorkingDirectory (Split-Path -Parent $resolvedGuardian) `
    -Wait `
    -PassThru

exit $process.ExitCode
