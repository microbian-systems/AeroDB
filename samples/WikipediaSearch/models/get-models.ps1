param(
    [string]$OutputDir = (Get-Location).Path
)

$baseUrl = "https://huggingface.co/nsense/all-MiniLM-L6-v2-onnx/resolve/main"
$files = @(
    "model.onnx",
    "tokenizer.json",
    "vocab.txt",
    "config.json"
)

Write-Host "Downloading all-MiniLM-L6-v2 ONNX model files to: $OutputDir" -ForegroundColor Cyan

foreach ($file in $files) {
    $url = "$baseUrl/$file"
    $outPath = Join-Path $OutputDir $file

    Write-Host "Downloading $file..." -ForegroundColor Yellow

    $wc = [System.Net.WebClient]::new()
    $reg = Register-ObjectEvent -InputObject $wc -Event DownloadProgressChanged -Action {
        $e = $EventArgs
        $pct = $e.ProgressPercentage
        $got = [math]::Round($e.BytesReceived / 1KB)
        $total = if ($e.TotalBytesToReceive -gt 0) { [math]::Round($e.TotalBytesToReceive / 1KB) } else { $null }
        if ($total) {
            Write-Progress -Activity $file -Status "${got} KB / ${total} KB" -PercentComplete $pct
        } else {
            Write-Progress -Activity $file -Status "${got} KB downloaded" -PercentComplete $pct
        }
    }

    try {
        $wc.DownloadFileAsync($url, $outPath)
        while ($wc.IsBusy) { Start-Sleep -Milliseconds 100 }
        Unregister-Event -SourceIdentifier $reg.Name -ErrorAction SilentlyContinue
        Write-Progress -Activity $file -Completed
        $size = (Get-Item $outPath).Length
        Write-Host "  Saved $file ($([math]::Round($size / 1KB)) KB)" -ForegroundColor Green
    } catch {
        Unregister-Event -SourceIdentifier $reg.Name -ErrorAction SilentlyContinue
        Write-Progress -Activity $file -Completed
        Write-Host "  Failed to download $file : $_" -ForegroundColor Red
    } finally {
        $wc.Dispose()
    }
}

Write-Host "Done." -ForegroundColor Cyan
