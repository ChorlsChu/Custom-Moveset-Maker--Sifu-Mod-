$FilePath = "C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Defense\FireDisciple_Adv_ContextualDefense.uasset"
$bytes = [System.IO.File]::ReadAllBytes($FilePath)
Write-Host "File size: $($bytes.Length) bytes"

# Look for "None" as a raw string anywhere
$noneBytes = [System.Text.Encoding]::ASCII.GetBytes("None")
for ($i = 0; $i -lt $bytes.Length - 4; $i++) {
    if ($bytes[$i] -eq $noneBytes[0] -and $bytes[$i+1] -eq $noneBytes[1] -and $bytes[$i+2] -eq $noneBytes[2] -and $bytes[$i+3] -eq $noneBytes[3]) {
        # Check if preceded by length=4
        if ($i -ge 4) {
            $prevLen = [System.BitConverter]::ToInt32($bytes, $i - 4)
            if ($prevLen -eq 4) {
                Write-Host "Found 'None' with length prefix at offset 0x$(($i-4).ToString('X4'))"
            }
        }
        Write-Host "Found 'None' string at offset 0x$($i.ToString('X4')) (preceding 8 bytes: $([System.BitConverter]::ToString($bytes, [Math]::Max(0,$i-8), 8)))"
    }
}

# Also look for "m_fMemoryLimit" string
$targetStr = [System.Text.Encoding]::ASCII.GetBytes("m_fMemoryLimit")
for ($i = 0; $i -lt $bytes.Length - 15; $i++) {
    $match = $true
    for ($j = 0; $j -lt $targetStr.Length; $j++) {
        if ($bytes[$i + $j] -ne $targetStr[$j]) { $match = $false; break }
    }
    if ($match) {
        Write-Host "Found 'm_fMemoryLimit' at offset 0x$($i.ToString('X4'))"
        # Check preceding 4 bytes for length
        if ($i -ge 4) {
            $prevLen = [System.BitConverter]::ToInt32($bytes, $i - 4)
            Write-Host "  Preceding int32: $prevLen (0x$($prevLen.ToString('X4')))"
        }
    }
}

# Dump hex of first 128 bytes
Write-Host "`nFirst 128 bytes:"
for ($row = 0; $row -lt 8; $row++) {
    $offset = $row * 16
    $hex = ""
    $ascii = ""
    for ($c = 0; $c -lt 16; $c++) {
        $b = $bytes[$offset + $c]
        $hex += "{0:X2} " -f $b
        if ($b -ge 32 -and $b -le 126) { $ascii += [char]$b } else { $ascii += "." }
    }
    Write-Host ("{0:X4}: {1} {2}" -f $offset, $hex, $ascii)
}
