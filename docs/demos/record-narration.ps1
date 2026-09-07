[CmdletBinding()]
param([ValidateSet('h1', 'h2', 'h3')][string]$Scenario = 'h1')

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Narration generation requires Windows SAPI desktop voices.' }
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = Join-Path $repositoryRoot "artifacts/$Scenario-video"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$scenes = Get-Content (Join-Path $repositoryRoot "tests/Workbench.BrowserTests/demos/$Scenario-narration.json") -Raw | ConvertFrom-Json
$voice = New-Object -ComObject SAPI.SpVoice
$voice.Voice = @($voice.GetVoices() | Where-Object { $_.GetDescription() -like '*Zira*' })[0]
$voice.Rate = 0
$durations = @{}
foreach ($scene in $scenes) {
    $stream = New-Object -ComObject SAPI.SpFileStream
    try {
        # SAFT22kHz16BitMono; deterministic uncompressed audio for subsequent mixing.
        $stream.Format.Type = 22
        $stream.Open((Join-Path $output "$($scene.id).wav"), 3, $false)
        $voice.AudioOutputStream = $stream
        [void]$voice.Speak($scene.text)
    }
    finally { $stream.Close() }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $output "$($scene.id).wav"))
    $byteRate = [BitConverter]::ToInt32($bytes, 28)
    $offset = 12
    while ([Text.Encoding]::ASCII.GetString($bytes, $offset, 4) -ne 'data') {
        $size = [BitConverter]::ToInt32($bytes, $offset + 4)
        $offset += 8 + $size + ($size % 2)
    }
    $durations[$scene.id] = [BitConverter]::ToInt32($bytes, $offset + 4) / $byteRate
}
$durations | ConvertTo-Json | Set-Content (Join-Path $output 'durations.json')
Write-Host "Generated $($scenes.Count) $Scenario narration clips with Microsoft Zira (synthetic voice)."
