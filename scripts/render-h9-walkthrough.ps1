[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Ffmpeg,
    [Parameter(Mandatory)][string]$MediaRoot,
    [string]$Voice = 'Microsoft Zira Desktop'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$mediaRoot = (Resolve-Path -LiteralPath $MediaRoot).Path
if ($mediaRoot.TrimEnd('\').StartsWith($repositoryRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $mediaRoot -eq $repositoryRoot) { throw 'Walkthrough media must remain outside the repository.' }
$documentationRoot = Join-Path $repositoryRoot 'docs/demos/h9'
New-Item -ItemType Directory -Path $documentationRoot -Force | Out-Null
$encoder = (Resolve-Path -LiteralPath $Ffmpeg).Path
$segments = Get-Content -LiteralPath (Join-Path $mediaRoot 'segments.json') -Raw | ConvertFrom-Json
if (-not $segments -or -not (Test-Path -LiteralPath (Join-Path $mediaRoot 'capture.webm'))) {
    throw 'Run the H9 Playwright walkthrough before rendering.'
}
Add-Type -AssemblyName System.Speech
# Measure generated speech rather than assuming a chosen Windows voice fits its presentation hold.
function Get-WaveDuration([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'RIFF') { throw 'Expected RIFF narration.' }
        $null = $reader.ReadUInt32()
        if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'WAVE') { throw 'Expected WAVE narration.' }
        $byteRate = 0; $dataLength = 0
        while ($reader.BaseStream.Position + 8 -le $reader.BaseStream.Length) {
            $name = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            $length = $reader.ReadUInt32()
            $next = $reader.BaseStream.Position + $length + ($length % 2)
            if ($name -eq 'fmt ') {
                $null = $reader.ReadBytes(8)
                $byteRate = $reader.ReadUInt32()
            } elseif ($name -eq 'data') { $dataLength = $length }
            $reader.BaseStream.Position = $next
        }
        if ($byteRate -le 0 -or $dataLength -le 0) { throw 'Narration has no measurable audio.' }
        return $dataLength / [double]$byteRate
    } finally { $reader.Dispose() }
}
$captions = New-Object Collections.Generic.List[string]
$transcript = New-Object Collections.Generic.List[string]
$transcript.Add('# H9 acquisition walkthrough transcript')
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
    $duration = Get-WaveDuration (Join-Path $mediaRoot $wav)
    $slot = [double]$segment.end - [double]$segment.start
    if ($slot -le 0) { throw "Invalid narration timing for segment $number." }
    $tempo = [Math]::Max(1, $duration / $slot)
    if ($tempo -gt 1.25) { throw "Segment $number needs a longer presentation hold for voice '$Voice'." }
    $tempoText = $tempo.ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture)
    $delay = [int][Math]::Round($segment.start * 1000)
    $filters.Add("[$($number):a]atempo=$tempoText,adelay=$($delay):all=1[a$number]")
    $inputs += "[a$number]"
    $start = [TimeSpan]::FromSeconds($segment.start).ToString('hh\:mm\:ss\,fff')
    $end = [TimeSpan]::FromSeconds($segment.end).ToString('hh\:mm\:ss\,fff')
    $captions.Add("$number`n$start --> $end`n$($segment.text)`n")
    $transcript.Add("`n$($segment.text)")
}
$endSeconds = ([double]$segments[-1].end).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture)
$filters.Add("$($inputs)amix=inputs=$($segments.Count):duration=longest:normalize=0,apad=whole_dur=$endSeconds[narration]")
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $mediaRoot 'captions.srt'), ($captions -join "`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $documentationRoot 'captions.srt'), ($captions -join "`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $documentationRoot 'transcript.md'), ($transcript -join "`n") + "`n", $utf8)
$arguments += @('-filter_complex', ($filters -join ';'), '-map', '0:v', '-map', '[narration]',
    '-vf', "pad=iw:ih+140:0:0:color=0x181818,subtitles=captions.srt:force_style='FontSize=8,MarginV=8,Outline=1'",
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '22', '-pix_fmt', 'yuv420p',
    '-c:a', 'aac', '-b:a', '128k', '-shortest', '-movflags', '+faststart', 'h9-acquisition.mp4')
Push-Location $mediaRoot
try {
    & $encoder @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Walkthrough render failed.' }
    & $encoder -v error -i h9-acquisition.mp4 -f null -
    if ($LASTEXITCODE -ne 0) { throw 'Walkthrough decode verification failed.' }
} finally { Pop-Location }
Write-Host "Rendered and decoded $(Join-Path $mediaRoot 'h9-acquisition.mp4')"
