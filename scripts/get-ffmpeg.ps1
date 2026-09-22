# Downloads a portable FFmpeg build into tools/ffmpeg so nothing needs to be installed system-wide.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
$dest = Join-Path $tools "ffmpeg"
if (Test-Path (Join-Path $dest "bin\ffmpeg.exe")) { Write-Host "ffmpeg already present at $dest"; exit 0 }

New-Item -ItemType Directory -Force $tools | Out-Null
$zip = Join-Path $tools "ffmpeg.zip"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
Write-Host "downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $tools -Force
$extracted = Get-ChildItem $tools -Directory | Where-Object { $_.Name -like "ffmpeg-master-*" } | Select-Object -First 1
Move-Item $extracted.FullName $dest
Remove-Item $zip
& (Join-Path $dest "bin\ffmpeg.exe") -version | Select-Object -First 1
