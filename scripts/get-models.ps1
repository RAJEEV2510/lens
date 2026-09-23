# Downloads the models into models/:
#   yolov10n.onnx       YOLOv10n, COCO, 80 classes (~9 MB)
#   yolov11n-face.onnx  YOLOv11n face detector, 1 class, Apache-2.0 (~10 MB). Optional; without it faces are skipped.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "models"
New-Item -ItemType Directory -Force $models | Out-Null

$downloads = [ordered]@{
    "yolov10n.onnx"      = "https://huggingface.co/onnx-community/yolov10n/resolve/main/onnx/model.onnx"
    "yolov11n-face.onnx" = "https://huggingface.co/AdamCodd/YOLOv11n-face-detection/resolve/main/model.onnx"
}

foreach ($name in $downloads.Keys) {
    $target = Join-Path $models $name
    if (Test-Path $target) { Write-Host "model already present at $target"; continue }
    $url = $downloads[$name]
    Write-Host "downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $target
    Write-Host ("saved {0} ({1:N0} bytes)" -f $target, (Get-Item $target).Length)
}
