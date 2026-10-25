# Creates isolated MiSide test installs next to the project (never touches the real install).
# Big read-only content (assets, voice and text packs) is hardlinked, everything else is copied,
# so a test install costs ~100 MB instead of ~4 GB. Hardlinks need the same NTFS volume.
param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\MiSide',
    [string]$Dest = (Join-Path $PSScriptRoot '..\local\installs'),
    [string[]]$Names = @('A', 'B')
)
$ErrorActionPreference = 'Stop'

$hardlinkPatterns = @('*.assets', '*.resS', '*.resource', 'level*', 'globalgamemanagers*')
function Test-Hardlinkable([System.IO.FileInfo]$f, [string]$rel) {
    if ($rel -like 'Data\Languages\*' -or $rel -like 'Data\LanguagesVoice\*') { return $true }
    if ($rel -like 'MiSideFull_Data\*' -and $rel.Split('\').Count -eq 2) {
        foreach ($p in $hardlinkPatterns) { if ($f.Name -like $p) { return $true } }
    }
    return $false
}

$Dest = [System.IO.Path]::GetFullPath($Dest)
$files = Get-ChildItem $GameDir -Recurse -File | Where-Object { $_.FullName.Substring($GameDir.Length + 1) -notlike 'Voice Editor\*' }
foreach ($name in $Names) {
    $target = Join-Path $Dest $name
    if (Test-Path (Join-Path $target 'MiSideFull.exe')) { Write-Host "skip $name (exists)"; continue }
    $linked = 0; $copied = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($GameDir.Length + 1)
        $out = Join-Path $target $rel
        $dir = Split-Path $out -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        if (Test-Hardlinkable $f $rel) {
            New-Item -ItemType HardLink -Path $out -Target $f.FullName | Out-Null; $linked++
        } else {
            Copy-Item -LiteralPath $f.FullName -Destination $out; $copied++
        }
    }
    # Keeps SteamAPI from relaunching the game through Steam into the real install.
    Set-Content -Encoding ascii -NoNewline -Path (Join-Path $target 'steam_appid.txt') -Value '2527500'
    Write-Host "$name ready: $linked hardlinked, $copied copied -> $target"
}
