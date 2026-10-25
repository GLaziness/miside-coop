# MiSide Co-op: installs or removes the mod in the game folder. install.bat, uninstall.bat and
# uninstall-all.bat call it. Works only inside the game folder; saves are never touched.
#
#   setup.ps1 [-Action Install|Uninstall|UninstallAll] [-GameDir <folder>] [-Yes] [-DryRun]
#
# -GameDir   the folder with MiSideFull.exe (default: found through Steam)
# -Yes       no questions (answers "yes")
# -DryRun    only show the folder and what would be done
param(
    [ValidateSet('Install', 'Uninstall', 'UninstallAll')] [string]$Action = 'Install',
    [string]$GameDir = '',
    [switch]$Yes,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Payload = Join-Path $Here 'game'
$AppId = '2527500'
$Exe = 'MiSideFull.exe'
# GameAssembly.dll of the build the mod was tested on (Steam build 19029065, version 0.93L).
$TestedGameAssembly = '96346a02014f4e1efad20e7d433b474a8e558fcffcec18b7f15c219ffacf46e5'
$Marker = 'BepInEx\miside-coop-installed.txt'
$PluginDir = 'BepInEx\plugins\MiSideCoop'
# What BepInEx puts into the game folder besides BepInEx\.
$LoaderFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
$LoaderDirs = @('BepInEx', 'dotnet')

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Stop-Setup([string]$text) { Say $text 'Red'; exit 1 }

function Ask([string]$question) {
    if ($Yes) { return $true }
    $a = Read-Host "$question (y/n)"
    return $a -match '^\s*(y|yes|д|да)\s*$'
}

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') { if ($p.$name) { $roots += ($p.$name -replace '/', '\') } }
        } catch { }
    }
    $libs = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        $libs += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -Raw -LiteralPath $vdf), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    return $libs | Select-Object -Unique
}

function Find-Game {
    foreach ($lib in Get-SteamLibraries) {
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $m = [regex]::Match((Get-Content -Raw -LiteralPath $acf), '"installdir"\s+"([^"]+)"')
        if (-not $m.Success) { continue }
        $dir = Join-Path $lib ('steamapps\common\' + $m.Groups[1].Value)
        if (Test-Path -LiteralPath (Join-Path $dir $Exe)) { return $dir }
    }
    return $null
}

function Resolve-GameDir {
    $dir = $GameDir
    if (-not $dir) {
        $dir = Find-Game
        if ($dir) { Say "Нашёл MiSide: $dir" }
    }
    while (-not $dir -or -not (Test-Path -LiteralPath (Join-Path $dir $Exe))) {
        if ($dir) { Say "В папке '$dir' нет $Exe." 'Yellow' }
        if ($Yes) { Stop-Setup 'Папка игры не найдена. Укажи её: -GameDir <папка>.' }
        $dir = (Read-Host "Вставь путь к папке игры, где лежит $Exe (пусто — выход)").Trim().Trim('"')
        if (-not $dir) { exit 1 }
    }
    return [IO.Path]::GetFullPath($dir)
}

function Test-GameRunning([string]$dir) {
    $prefix = $dir.TrimEnd('\') + '\'
    $running = Get-Process -Name 'MiSideFull' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }
    return [bool]$running
}

function Read-Marker([string]$dir) {
    $path = Join-Path $dir $Marker
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-Content -LiteralPath $path | Where-Object { $_ -like 'bepinex=*' } | Select-Object -First 1) -replace '^bepinex=', ''
}

function Copy-Payload([string]$from, [string]$to) {
    foreach ($d in Get-ChildItem -LiteralPath $from -Recurse -Directory -Force) {
        $out = Join-Path $to $d.FullName.Substring($from.Length).TrimStart('\')
        if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }
    }
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $from -Recurse -File -Force) {
        $rel = $f.FullName.Substring($from.Length).TrimStart('\')
        $out = Join-Path $to $rel
        $outDir = Split-Path -Parent $out
        if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $out -Force
        try { Unblock-File -LiteralPath $out } catch { }
        $n++
    }
    return $n
}

function Install([string]$dir) {
    if (-not (Test-Path -LiteralPath (Join-Path $Payload $PluginDir))) {
        Stop-Setup "Рядом с установщиком нет папки 'game'. Распакуй архив целиком и запусти install.bat оттуда."
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dir 'GameAssembly.dll')).Hash.ToLowerInvariant()
    if ($hash -ne $TestedGameAssembly) {
        Say 'Версия игры отличается от той, на которой проверен мод (Steam, сборка 19029065).' 'Yellow'
        Say 'Мод может не работать. Если игра обновилась, проверь, нет ли новой версии мода.' 'Yellow'
        if (-not (Ask 'Всё равно установить?')) { exit 1 }
    }

    if (Test-Path -LiteralPath (Join-Path $dir 'MelonLoader')) {
        Say 'В игре стоит MelonLoader. С двумя загрузчиками модов игра может не запуститься.' 'Yellow'
        if (-not (Ask 'Продолжить?')) { exit 1 }
    }

    $ours = (Read-Marker $dir) -eq 'ours'
    $haveCore = Test-Path -LiteralPath (Join-Path $dir 'BepInEx\core')
    $haveProxy = Test-Path -LiteralPath (Join-Path $dir 'winhttp.dll')
    $installLoader = $ours -or (-not $haveCore -and -not $haveProxy)
    if (-not $installLoader -and -not $haveCore) {
        Stop-Setup 'В папке игры уже есть winhttp.dll от другого мода. Не перезаписываю его: удали этот мод или поставь наш вручную.'
    }
    if (-not $installLoader) {
        Say 'BepInEx уже установлен, его не трогаю, ставлю только мод.' 'Yellow'
        $core = Join-Path $dir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
        if (Test-Path -LiteralPath $core) {
            $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($core).ProductVersion
            if ($v -notlike '6.0.0-be.755*') { Say "Его версия $v, мод проверен с 6.0.0-be.755." 'Yellow' }
        } else {
            Say 'Это не BepInEx 6 для IL2CPP, мод с ним работать не будет.' 'Yellow'
        }
    }

    if ($DryRun) {
        Say ("DryRun: поставил бы " + ($(if ($installLoader) { 'BepInEx и мод' } else { 'только мод' })) + " в $dir") 'Cyan'
        return
    }

    if ($installLoader) {
        $n = Copy-Payload $Payload $dir
    } else {
        $n = Copy-Payload (Join-Path $Payload $PluginDir) (Join-Path $dir $PluginDir)
    }
    Set-Content -LiteralPath (Join-Path $dir $Marker) -Encoding ascii -Value @(
        "bepinex=$(if ($installLoader) { 'ours' } else { 'preexisting' })",
        "installed=$(Get-Date -Format s)")
    Say "Готово: скопировано файлов: $n." 'Green'
    Say 'Запусти MiSide через Steam. Первый запуск дольше обычного и нужен интернет: BepInEx скачивает библиотеки Unity и готовит файлы.' 'Green'
    Say 'В игре меню кооператива открывается клавишей F7.' 'Green'
}

function Uninstall([string]$dir, [bool]$all) {
    $plugin = Join-Path $dir $PluginDir
    $loaderBy = Read-Marker $dir
    $removeLoader = $false
    if ($all) {
        if ($loaderBy -eq 'ours') {
            $others = @()
            foreach ($sub in 'plugins', 'patchers') {
                $p = Join-Path $dir "BepInEx\$sub"
                if (Test-Path -LiteralPath $p) {
                    $others += Get-ChildItem -LiteralPath $p -Force | Where-Object { $_.Name -ne 'MiSideCoop' }
                }
            }
            if ($others) {
                Say ('В BepInEx есть другие моды (' + (($others | ForEach-Object Name) -join ', ') + '), BepInEx оставляю.') 'Yellow'
            } else {
                $removeLoader = $true
            }
        } elseif ($loaderBy -eq 'preexisting') {
            Say 'BepInEx стоял до установки мода, его не трогаю.' 'Yellow'
        } elseif (Test-Path -LiteralPath (Join-Path $dir 'BepInEx')) {
            Say 'BepInEx ставил не этот установщик, его не трогаю.' 'Yellow'
        }
    }

    if ($DryRun) {
        Say ("DryRun: убрал бы мод" + $(if ($removeLoader) { ' и BepInEx' } else { '' }) + " из $dir") 'Cyan'
        return
    }

    $hadPlugin = Test-Path -LiteralPath $plugin
    if ($hadPlugin) { Remove-Item -LiteralPath $plugin -Recurse -Force }
    if ($removeLoader) {
        foreach ($f in $LoaderFiles) {
            $p = Join-Path $dir $f
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
        }
        foreach ($d in $LoaderDirs) {
            $p = Join-Path $dir $d
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
        }
        Say 'Мод и BepInEx удалены.' 'Green'
    } else {
        if ($all) {
            foreach ($p in (Join-Path $dir 'BepInEx\config\miside.coop.cfg'), (Join-Path $dir $Marker)) {
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
            }
        }
        if ($hadPlugin) { Say 'Мод удалён.' 'Green' } else { Say "Мода в $dir нет." 'Yellow' }
    }
}

$dir = Resolve-GameDir
if (Test-GameRunning $dir) { Stop-Setup 'MiSide сейчас запущена. Закрой игру и запусти установщик снова.' }
switch ($Action) {
    'Install' { Install $dir }
    'Uninstall' { Uninstall $dir $false }
    'UninstallAll' { Uninstall $dir $true }
}
