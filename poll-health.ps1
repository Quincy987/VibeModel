for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Seconds 10
    try {
        $r = Invoke-WebRequest -Uri 'http://localhost:18884/health' -UseBasicParsing -TimeoutSec 3
        Write-Host "VibeModel is UP: $($r.Content)"
        exit 0
    } catch {
        Write-Host "Attempt $($i+1): Revit still loading..."
    }
}
Write-Host "Timed out waiting for VibeModel"
exit 1
