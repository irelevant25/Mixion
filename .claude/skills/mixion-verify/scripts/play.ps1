param([string]$Wav, [string]$PidFile, [int]$HoldSeconds = 5)

# Opens an audio session (with silent content) and keeps the process alive for a while.
$PID | Out-File -FilePath $PidFile -Encoding ascii
$player = New-Object System.Media.SoundPlayer $Wav
$player.PlaySync()
Start-Sleep -Seconds $HoldSeconds
