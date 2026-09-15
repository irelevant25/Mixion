param(
    [int]$Port = 54917,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path,
    [string]$Configuration = 'Debug'
)

# Live end-to-end check of the Mixion host. See ../SKILL.md for what it proves.

$ErrorActionPreference = 'Stop'

$scripts = $PSScriptRoot
$work    = Join-Path $env:TEMP 'mixion-smoke'
$exe     = Join-Path $RepoRoot "BE\Mixion.Host\bin\$Configuration\net8.0-windows\win-x64\Mixion.exe"
$wav     = Join-Path $work 'silence.wav'
$app     = Join-Path $work 'mixsound.exe'

if (-not (Test-Path $exe)) { throw "Host not built: $exe (run 'dotnet build Mixion.sln')." }
if (Get-Process -Name 'Mixion' -ErrorAction SilentlyContinue) {
    throw 'A Mixion process is already running; the single-instance gate would forward to it.'
}

New-Item -ItemType Directory -Force -Path $work | Out-Null
Remove-Item (Join-Path $work '*.txt'), (Join-Path $work 'ws.*') -ErrorAction SilentlyContinue

# 1.5 s of 48 kHz / 16-bit mono silence — opens an audio session without making a sound.
$rate = 48000; $dataLength = [int]($rate * 1.5) * 2
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $dataLength))
$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16); $writer.Write([int16]1)
$writer.Write([int16]1); $writer.Write([int]$rate); $writer.Write([int]($rate * 2)); $writer.Write([int16]2); $writer.Write([int16]16)
$writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$dataLength); $writer.Write((New-Object byte[] $dataLength))
[IO.File]::WriteAllBytes($wav, $stream.ToArray())

# A uniquely named "app", so no other process on the machine shares its name.
Copy-Item (Join-Path $PSHOME 'powershell.exe') $app -Force

$logDir   = Join-Path $env:LOCALAPPDATA 'Mixion\logs'
$logFile  = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$logStart = if ($logFile) { $logFile.Length } else { 0 }

function Start-SoundApp([string]$pidFile) {
    # Started through `cmd /c start` so its parent is gone and it is its own root process.
    $arguments = "/c start `"`" /min `"$app`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scripts\play.ps1`" -Wav `"$wav`" -PidFile `"$pidFile`""
    Start-Process -FilePath cmd.exe -ArgumentList $arguments -WindowStyle Hidden
}

$mixion = Start-Process -FilePath $exe -ArgumentList '--no-browser', '--no-driver-check', '--port', $Port -PassThru
$client = Start-Process -FilePath node -ArgumentList "`"$scripts\wsclient.mjs`"", $Port -PassThru -WindowStyle Hidden `
          -RedirectStandardOutput (Join-Path $work 'ws.log') -RedirectStandardError (Join-Path $work 'ws.err')

$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not ((Get-Content (Join-Path $work 'ws.log') -ErrorAction SilentlyContinue) -match 'getState')) {
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Seconds 2

Start-SoundApp (Join-Path $work 'app-pid-1.txt')
Start-Sleep -Seconds 11      # plays, holds 5 s, exits — the watcher should detach it
Start-SoundApp (Join-Path $work 'app-pid-2.txt')
Start-Sleep -Seconds 5       # a new instance — the watcher should re-attach the channel

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
Start-Process powershell.exe -WindowStyle Hidden -Wait `
    -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scripts\sendctrlc.ps1`" -TargetPid $($mixion.Id) -Out `"$work\ctrlc.txt`""
$exited  = $mixion.WaitForExit(30000)
$elapsed = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
if (-not $exited) { Stop-Process -Id $mixion.Id -Force }
$client.WaitForExit(5000) | Out-Null

$transcript = Get-Content (Join-Path $work 'ws.log') -Raw -ErrorAction SilentlyContinue
if (-not $transcript) { $transcript = '' }

"=== websocket client"
$transcript
Get-Content (Join-Path $work 'ws.err') -ErrorAction SilentlyContinue
"=== ctrl+c: $(Get-Content (Join-Path $work 'ctrlc.txt') -ErrorAction SilentlyContinue)"
"=== host exited=$exited after ${elapsed}s, exit code $(if ($exited) { $mixion.ExitCode } else { 'n/a (killed)' })"

"=== host log (this run)"
$logFile = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$fs = [IO.File]::Open($logFile.FullName, 'Open', 'Read', 'ReadWrite')
if ($fs.Length -ge $logStart) { $fs.Seek($logStart, 'Begin') | Out-Null }
$text = (New-Object IO.StreamReader($fs)).ReadToEnd()
$fs.Dispose()
$text -split "`r?`n" |
    Where-Object { $_ -match 'mixsound|Re-attached|Detached|Attached new|WS |Shutdown|stopped|listening|block size|WARN|ERROR|xception' } |
    Select-Object -First 80

"=== checks"
$checks = [ordered]@{
    'state available on connect' = $transcript -match 'getState: [1-9]\d* inputs'
    'meter frames flowing'       = $transcript -match 'frames=[1-9]'
    'app attached'               = $transcript -match 'process:mixsound=up'
    'app detached after exit'    = $transcript -match 'process:mixsound=up[\s\S]*process:mixsound=down'
    'app re-attached on restart' = $transcript -match 'process:mixsound=down[\s\S]*process:mixsound=up'
    'hostShutdown notification'  = $transcript -match 'notification: hostShutdown'
    'socket closed 1001'         = $transcript -match 'code=1001 reason=host-shutdown'
    'host exited with code 0'    = $exited -and $mixion.ExitCode -eq 0
}
foreach ($check in $checks.GetEnumerator()) {
    '{0,-28} {1}' -f $check.Key, $(if ($check.Value) { 'PASS' } else { 'FAIL' })
}
