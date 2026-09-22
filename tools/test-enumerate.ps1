$ContentPath = "C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content"

$uassetPaths = @()
try {
    $uassetPaths = @([System.IO.Directory]::EnumerateFiles($ContentPath, "*ContextualDefense*.uasset", [System.IO.SearchOption]::AllDirectories))
    Write-Host "EnumerateFiles returned $($uassetPaths.Count) results"
} catch {
    Write-Host "EnumerateFiles failed: $_"
}

$dbPath = Join-Path $ContentPath "DB\AI"
Write-Host "DB\AI path exists: $(Test-Path $dbPath)"

$uassetPaths2 = @()
try {
    $uassetPaths2 = @([System.IO.Directory]::EnumerateFiles($dbPath, "*ContextualDefense*.uasset", [System.IO.SearchOption]::AllDirectories))
    Write-Host "DB\AI fallback returned $($uassetPaths2.Count) results"
} catch {
    Write-Host "DB\AI fallback failed: $_"
}

# Try Get-ChildItem on just DB\AI
$gci = @(Get-ChildItem -Recurse -Filter "*ContextualDefense*.uasset" -Path $dbPath -ErrorAction SilentlyContinue)
Write-Host "Get-ChildItem on DB\AI returned $($gci.Count) results"
if ($gci.Count -gt 0) {
    $gci | Select-Object -First 3 | ForEach-Object { Write-Host "  $($_.FullName)" }
}
