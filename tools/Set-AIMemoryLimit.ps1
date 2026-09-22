<#
.SYNOPSIS
    Modifies m_fMemoryLimit values in Sifu's ContextualDefense .uexp files.
.DESCRIPTION
    Uses UE4 name table parsing to precisely locate m_fMemoryLimit properties
    in binary .uexp files. Supports dry-run, per-unit filtering, and outputs
    a defaults JSON for future "reset to default" functionality.
.PARAMETER ContentPath
    Path to the extracted pak Content folder
.PARAMETER Multiplier
    Multiply all m_fMemoryLimit values by this factor. Default: 1.5
.PARAMETER MinValue
    Minimum allowed value (floor). Default: 2.0
.PARAMETER MaxValue
    Maximum allowed value (ceiling). Default: 50.0
.PARAMETER DryRun
    Preview changes without writing to files.
.PARAMETER SpecificUnit
    Only modify files whose name contains this string.
.PARAMETER DefaultsPath
    Output path for the defaults JSON file.
.EXAMPLE
    .\Set-AIMemoryLimit.ps1 -ContentPath "C:\...\Content" -DryRun
.EXAMPLE
    .\Set-AIMemoryLimit.ps1 -ContentPath "C:\...\Content" -Multiplier 2.0 -SpecificUnit "FireDisciple"
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$ContentPath,
    [float]$Multiplier = 1.5,
    [float]$MinValue = 2.0,
    [float]$MaxValue = 50.0,
    [switch]$DryRun,
    [string]$SpecificUnit = "",
    [string]$DefaultsPath = ".\AI_MemoryDefaults.json"
)

function Get-UAssetNameTable {
    param([string]$FilePath)
    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    if ($bytes.Length -lt 16) { return @() }
    # UE4 magic: C1 83 2A 9E (little-endian)
    if ($bytes[0] -ne 0xC1 -or $bytes[1] -ne 0x83 -or $bytes[2] -ne 0x2A -or $bytes[3] -ne 0x9E) { return @() }
    $version = [System.BitConverter]::ToInt32($bytes, 4)
    if ($version -ne -7) { return @() }

    $names = [System.Collections.ArrayList]::new()
    $nameIndex = 1

    for ($start = 0x20; $start -lt [Math]::Min($bytes.Length - 16, 0x800); $start += 4) {
        $offset = $start
        $valid = $true
        $parsed = 0

        while ($offset -lt $bytes.Length - 8 -and $parsed -lt 10) {
            $nameLen = [System.BitConverter]::ToInt32($bytes, $offset)
            if ($nameLen -le 0 -or $nameLen -gt 300) { $valid = $false; break }
            $offset += 4
            $allAscii = $true
            for ($j = 0; $j -lt [Math]::Min($nameLen, 60); $j++) {
                $b = $bytes[$offset + $j]
                if ($j -eq $nameLen - 1) {
                    if ($b -ne 0) { $allAscii = $false }
                } elseif ($b -lt 1 -or $b -gt 126) { $allAscii = $false; break }
            }
            if (-not $allAscii) { $valid = $false; break }
            $offset += $nameLen
            $offset += 4
            $parsed++
        }

        if ($valid -and $parsed -ge 10) {
            $offset = $start
            while ($offset -lt $bytes.Length - 8) {
                $nameLen = [System.BitConverter]::ToInt32($bytes, $offset)
                if ($nameLen -le 0 -or $nameLen -gt 500) { break }
                $offset += 4
                $nameStr = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, $nameLen).TrimEnd([char]0)
                $offset += $nameLen
                $offset += 4
                [void]$names.Add(@{ Index = $nameIndex; Name = $nameStr })
                $nameIndex++
            }
            break
        }
    }
    Write-Host "  Parsed $($names.Count) names from name table"
    return $names.ToArray()
}

function Find-MemoryLimitEntries {
    param(
        [string]$UexpPath,
        [int]$MemoryLimitIdx,
        [int]$FloatPropertyIdx
    )
    $bytes = [System.IO.File]::ReadAllBytes($UexpPath)
    $entries = [System.Collections.ArrayList]::new()

    for ($i = 0; $i -lt $bytes.Length - 28; $i += 4) {
        if ([System.BitConverter]::ToInt32($bytes, $i) -ne $MemoryLimitIdx) { continue }
        if ([System.BitConverter]::ToInt32($bytes, $i + 4) -ne 0) { continue }
        if ([System.BitConverter]::ToInt32($bytes, $i + 8) -ne $FloatPropertyIdx) { continue }
        if ([System.BitConverter]::ToInt32($bytes, $i + 12) -ne 0) { continue }
        if ([System.BitConverter]::ToInt32($bytes, $i + 16) -ne 4) { continue }
        if ([System.BitConverter]::ToInt32($bytes, $i + 20) -ne 0) { continue }
        if ($bytes[$i + 24] -ne 0) { continue }

        $floatVal = [System.BitConverter]::ToSingle($bytes, $i + 25)
        if ([Math]::Abs($floatVal) -lt 0.01 -or [Math]::Abs($floatVal) -gt 1000) { continue }

        $b0 = $bytes[$i+25]; $b1 = $bytes[$i+26]; $b2 = $bytes[$i+27]; $b3 = $bytes[$i+28]
        [void]$entries.Add(@{
            Offset = $i + 25
            DefaultValue = $floatVal
            OriginalHex = ("{0:X2} {1:X2} {2:X2} {3:X2}" -f $b0, $b1, $b2, $b3)
        })
    }
    return ,($entries.ToArray())
}

# --- Main ---
Write-Host "=== Sifu AI Memory Limit Modifier ===" -ForegroundColor Cyan
Write-Host "Content path: $ContentPath"
Write-Host "Multiplier: ${Multiplier}x"
Write-Host "Range: $MinValue - $MaxValue"
Write-Host "Dry run: $DryRun"
if ($SpecificUnit) { Write-Host "Filter: $SpecificUnit" }
Write-Host ""

# Find files using .NET (handles long paths and broken symlinks)
$uassetPaths = @()
try {
    Write-Host "Searching from: $ContentPath"
    $uassetPaths = @([System.IO.Directory]::EnumerateFiles($ContentPath, "*ContextualDefense*.uasset", [System.IO.SearchOption]::AllDirectories))
    Write-Host "EnumerateFiles found: $($uassetPaths.Count)"
} catch {
    Write-Host "Primary search failed: $($_.Exception.Message)"
    $dbPath = Join-Path $ContentPath "DB\AI"
    if (Test-Path $dbPath) {
        Write-Host "Trying fallback from: $dbPath"
        $uassetPaths = @([System.IO.Directory]::EnumerateFiles($dbPath, "*ContextualDefense*.uasset", [System.IO.SearchOption]::AllDirectories))
        Write-Host "Fallback found: $($uassetPaths.Count)"
    }
}

# Filter
    # Only filter on the relative path after Content, not the full path (which may contain "Modding" etc.)
    $uassetPaths = @($uassetPaths | Where-Object {
        $rel = $_.Replace($ContentPath, "")
        $rel -notlike "*Backup*" -and $rel -notlike "*_Mod*"
    })
Write-Host "After backup/mod filter: $($uassetPaths.Count)"
if ($SpecificUnit) {
    $uassetPaths = @($uassetPaths | Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) -like "*$SpecificUnit*" })
    Write-Host "After unit filter ($SpecificUnit): $($uassetPaths.Count)"
    if ($uassetPaths.Count -le 3) {
        foreach ($p in $uassetPaths) { Write-Host "  $p" }
    }
}

Write-Host "Found $($uassetPaths.Count) ContextualDefense assets" -ForegroundColor Green
Write-Host ""

$defaults = @{}
$totalEntries = 0
$totalModified = 0

foreach ($uassetPath in $uassetPaths) {
    $uexpPath = $uassetPath -replace '\.uasset$', '.uexp'
    if (-not (Test-Path $uexpPath)) { continue }

    $unitName = [System.IO.Path]::GetFileNameWithoutExtension($uassetPath)
    Write-Host "Processing: $unitName" -ForegroundColor Yellow

    $nameTable = Get-UAssetNameTable -FilePath $uassetPath
    if ($nameTable.Count -eq 0) {
        Write-Warning "  Could not parse name table, skipping"
        continue
    }

    $memoryLimitEntry = @($nameTable | Where-Object { $_.Name -eq "m_fMemoryLimit" })
    $floatPropEntry = @($nameTable | Where-Object { $_.Name -eq "FloatProperty" })

    if ($memoryLimitEntry.Count -eq 0) {
        Write-Host "  No m_fMemoryLimit in name table, skipping" -ForegroundColor DarkGray
        continue
    }
    if ($floatPropEntry.Count -eq 0) {
        Write-Warning "  No FloatProperty in name table, skipping"
        continue
    }

    $memoryLimitIdx = $memoryLimitEntry[0].Index
    $floatPropIdx = $floatPropEntry[0].Index

    $entries = Find-MemoryLimitEntries -UexpPath $uexpPath -MemoryLimitIdx $memoryLimitIdx -FloatPropertyIdx $floatPropIdx

    if ($entries.Count -eq 0) {
        Write-Host "  No m_fMemoryLimit values found in .uexp" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Found $($entries.Count) memory limit entries" -ForegroundColor Cyan

    $relativePath = $uassetPath.Replace($ContentPath, "").TrimStart("\", "/").Replace("\", "/")
    $defaults[$unitName] = @{ file = $relativePath; entries = [System.Collections.ArrayList]::new() }

    $uexpBytes = [System.IO.File]::ReadAllBytes($uexpPath)
    $fileModified = $false

    foreach ($entry in $entries) {
        $oldVal = $entry.DefaultValue
        $newVal = [Math]::Min($MaxValue, [Math]::Max($MinValue, $oldVal * $Multiplier))
        $newVal = [Math]::Round($newVal, 4)

        $null = $defaults[$unitName].entries.Add(@{
            index = $totalEntries
            offset = "0x{0:X4}" -f $entry.Offset
            defaultFloat = $oldVal
            originalHex = $entry.OriginalHex
        })

        $newBytes = [System.BitConverter]::GetBytes($newVal)
        $changed = ($newVal -ne $oldVal)

        if ($changed) {
            Write-Host "    [$totalEntries] 0x$($entry.Offset.ToString('X4')): $oldVal -> $newVal" -ForegroundColor White
            $totalModified++
            $fileModified = $true

            if (-not $DryRun) {
                $uexpBytes[$entry.Offset] = $newBytes[0]
                $uexpBytes[$entry.Offset + 1] = $newBytes[1]
                $uexpBytes[$entry.Offset + 2] = $newBytes[2]
                $uexpBytes[$entry.Offset + 3] = $newBytes[3]
            }
        } else {
            Write-Host "    [$totalEntries] 0x$($entry.Offset.ToString('X4')): $oldVal (unchanged)" -ForegroundColor DarkGray
        }
        $totalEntries++
    }

    if (-not $DryRun -and $fileModified) {
        [System.IO.File]::WriteAllBytes($uexpPath, $uexpBytes)
        $verifyBytes = [System.IO.File]::ReadAllBytes($uexpPath)
        $ok = $true
        foreach ($entry in $entries) {
            $oldVal = $entry.DefaultValue
            $newVal = [Math]::Min($MaxValue, [Math]::Max($MinValue, $oldVal * $Multiplier))
            $newVal = [Math]::Round($newVal, 4)
            if ($newVal -eq $oldVal) { continue }
            $readBack = [System.BitConverter]::ToSingle($verifyBytes, $entry.Offset)
            if ([Math]::Abs($readBack - $newVal) -gt 0.001) {
                Write-Warning "  VERIFY FAILED at 0x$($entry.Offset.ToString('X4')): expected $newVal, got $readBack"
                $ok = $false
            }
        }
        if ($ok) { Write-Host "  Written and verified OK" -ForegroundColor Green }
    }
    Write-Host ""
}

Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "Total entries found: $totalEntries"
Write-Host "Total modified: $totalModified"
Write-Host ""

# Save defaults JSON
$defaultsObj = @{
    generated = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    contentPath = $ContentPath
    multiplier = $Multiplier
    minValue = $MinValue
    maxValue = $MaxValue
    units = $defaults
}
$json = $defaultsObj | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($DefaultsPath, $json, [System.Text.Encoding]::UTF8)
Write-Host "Defaults saved to: $DefaultsPath" -ForegroundColor Green

if ($DryRun) {
    Write-Host ""
    Write-Host "DRY RUN - No files were modified" -ForegroundColor Yellow
}
