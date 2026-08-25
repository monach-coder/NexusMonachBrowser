param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp-translation-smoke')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$template = Join-Path $root 'tests\fixtures\translation-smoke\index.html'
$output = [IO.Path]::GetFullPath($OutputDirectory)

if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must stay inside the repository: $output"
}

Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath $template -Destination (Join-Path $output 'index.html') -Force

$shortText = @'
Welcome to the short Nexus translation test. This clip contains several complete English sentences.
The browser should start quickly, translate only fresh speech, and finish without leaving a delayed queue.
Privacy remains local, and the original audio should return to its previous volume when translation stops.
'@

$filmScenes = @(
    'Chapter one begins in a quiet research station beside the northern sea. The crew checks every instrument before sunrise.',
    'A sudden message changes the plan. The team must travel through the valley and compare several possible routes.',
    'During the journey, the guide explains why small details matter. Weather, distance, and timing influence every decision.',
    'The next scene introduces a historian who remembers the old city. Her account adds context that was missing before.',
    'At midday, the group pauses near a bridge. They review the evidence and reject an attractive but unsafe shortcut.',
    'The investigation continues inside a library. Names from earlier chapters return with a different meaning.',
    'By evening, two conversations overlap. The translator must preserve the newest phrase without repeating an older one.',
    'The final chapter resolves the main question. Everyone returns home, while the narrator summarizes the long journey.'
)
$filmText = 1..8 | ForEach-Object {
    "Part $_. " + ($filmScenes -join ' ')
}

function New-EnglishWave([string]$Path, [string]$Text, [int]$Rate) {
    $speaker = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $speaker.SelectVoice('Microsoft Zira Desktop')
        $speaker.Rate = $Rate
        $speaker.Volume = 90
        $speaker.SetOutputToWaveFile($Path)
        $speaker.Speak($Text)
    }
    finally {
        $speaker.Dispose()
    }
}

New-EnglishWave (Join-Path $output 'short.wav') $shortText 0
New-EnglishWave (Join-Path $output 'film.wav') ($filmText -join ' ') 0

Get-ChildItem -LiteralPath $output -File | Select-Object Name, Length, LastWriteTime
