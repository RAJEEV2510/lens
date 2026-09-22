# Downloads the YOLOv10n ONNX model (COCO, 80 classes, ~9 MB) into models/.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "models"
New-Item -ItemType Directory -Force $models | Out-Null
$target = Join-Path $models "yolov10n.onnx"
if (Test-Path $target) { Write-Host "model already present at $target"; exit 0 }

$url = "https://huggingface.co/onnx-community/yolov10n/resolve/main/onnx/model.onnx"
Write-Host "downloading $url"
Invoke-WebRequest -Uri $url -OutFile $target
Write-Host ("saved {0} ({1:N0} bytes)" -f $target, (Get-Item $target).Length)
