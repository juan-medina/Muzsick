$ErrorActionPreference = "Stop"
$modelsDir = "$PSScriptRoot\src\Muzsick\Models\KokoroModels"
$tempDir = "$PSScriptRoot\src\Muzsick\Models\temp"

if (Test-Path "$modelsDir\kokoro-en-v0_19\model.onnx") {
    Write-Host "Kokoro model already present, skipping download."
    exit 0
}

New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

$tarFile = "$tempDir\kokoro-en-v0_19.tar.bz2"
$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2"

Write-Host "Downloading Kokoro-82M model from $url ..."
Invoke-WebRequest -Uri $url -OutFile $tarFile -UseBasicParsing
Write-Host "Download complete ($([Math]::Round((Get-Item $tarFile).Length / 1MB, 1)) MB)"

Write-Host "Extracting..."
tar -xjf $tarFile -C $modelsDir
Write-Host "Extraction complete"

Remove-Item -Recurse -Force $tempDir
Write-Host "Done. Model files at: $modelsDir"
Get-ChildItem $modelsDir -Recurse | Select-Object FullName, Length

