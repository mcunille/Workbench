[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Ffmpeg,
    [string]$Voice = 'Microsoft Zira Desktop'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$mediaRoot = Join-Path $repositoryRoot 'artifacts/h7/walkthrough'
$documentationRoot = Join-Path $repositoryRoot 'docs/demos/h7'
$encoder = (Resolve-Path -LiteralPath $Ffmpeg).Path
$segments = Get-Content -LiteralPath (Join-Path $mediaRoot 'segments.json') -Raw | ConvertFrom-Json
if (-not $segments -or -not (Test-Path -LiteralPath (Join-Path $mediaRoot 'capture.webm'))) {
    throw 'Run the H7 Playwright walkthrough before rendering.'
}
Add-Type -AssemblyName System.Speech
$captions = New-Object Collections.Generic.List[string]
$transcript = New-Object Collections.Generic.List[string]
$transcript.Add('# H7 collection export walkthrough transcript')
$arguments = @('-y', '-hide_banner', '-i', 'capture.webm')
$filters = New-Object Collections.Generic.List[string]
$inputs = ''
for ($index = 0; $index -lt $segments.Count; $index++) {
    $segment = $segments[$index]
    $number = $index + 1
    $wav = "narration-$number.wav"
    $speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $speech.SelectVoice($Voice)
        $speech.Rate = 1
        $speech.SetOutputToWaveFile((Join-Path $mediaRoot $wav))
        $speech.Speak([string]$segment.text)
    } finally { $speech.Dispose() }
    $arguments += @('-i', $wav)
    $delay = [int][Math]::Round($segment.start * 1000)
    $filters.Add("[$($number):a]adelay=$($delay):all=1[a$number]")
    $inputs += "[a$number]"
    $start = [TimeSpan]::FromSeconds($segment.start).ToString('hh\:mm\:ss\,fff')
    $end = [TimeSpan]::FromSeconds($segment.end).ToString('hh\:mm\:ss\,fff')
    $captions.Add("$number`n$start --> $end`n$($segment.text)`n")
    $transcript.Add("`n$($segment.text)")
}
$filters.Add("$($inputs)amix=inputs=$($segments.Count):duration=longest:normalize=0[narration]")
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $mediaRoot 'captions.srt'), ($captions -join "`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $documentationRoot 'captions.srt'), ($captions -join "`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $documentationRoot 'transcript.md'), ($transcript -join "`n") + "`n", $utf8)
$arguments += @('-filter_complex', ($filters -join ';'), '-map', '0:v', '-map', '[narration]',
    '-vf', "pad=iw:ih+140:0:0:color=0x181818,subtitles=captions.srt:force_style='FontSize=8,MarginV=8,Outline=1'",
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '22', '-pix_fmt', 'yuv420p',
    '-c:a', 'aac', '-b:a', '128k', '-shortest', '-movflags', '+faststart', 'h7-collection-export.mp4')
Push-Location $mediaRoot
try {
    & $encoder @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Walkthrough render failed.' }
    & $encoder -v error -i h7-collection-export.mp4 -f null -
    if ($LASTEXITCODE -ne 0) { throw 'Walkthrough decode verification failed.' }
} finally { Pop-Location }
Write-Host "Rendered and decoded $(Join-Path $mediaRoot 'h7-collection-export.mp4')"
