# Download the all-MiniLM-L6-v2 ONNX model for the Wikipedia search demo
# Run from: samples/WikipediaSearch/

$ErrorActionPreference = "Stop"
$modelDir = Join-Path $PSScriptRoot "models\all-MiniLM-L6-v2"
New-Item -ItemType Directory -Path $modelDir -Force | Out-Null

$files = @{
    "model.onnx"     = "https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/model.onnx"
    "vocab.txt"      = "https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/vocab.txt"
    "config.json"    = "https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/config.json"
    "tokenizer.json" = "https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main/tokenizer.json"
}

foreach ($file in $files.GetEnumerator()) {
    $dest = Join-Path $modelDir $file.Key
    if (Test-Path $dest) {
        Write-Host "  Skipping $($file.Key) — already exists" -ForegroundColor Gray
        continue
    }
    Write-Host "  Downloading $($file.Key) ..." -NoNewline
    Invoke-WebRequest -Uri $file.Value -OutFile $dest
    Write-Host " done ($([math]::Round((Get-Item $dest).Length / 1MB, 1)) MB)" -ForegroundColor Green
}

Write-Host "`nModel files ready at: $modelDir" -ForegroundColor Cyan
Write-Host "You can now run: dotnet run" -ForegroundColor Yellow
