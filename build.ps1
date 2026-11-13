param(
    [string]$ValheimDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$BepInExDir,   # a BepInEx folder containing core\ and plugins\ (e.g. a Gale profile's BepInEx); autodetected from Gale if omitted
    [string]$Csc,          # path to a Roslyn csc.exe; autodetected from Visual Studio Build Tools if omitted
    [switch]$Package
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$managed = Join-Path $ValheimDir 'valheim_Data\Managed'
if (-not (Test-Path $managed)) { throw "Valheim Managed folder not found: $managed (use -ValheimDir)" }

if (-not $BepInExDir) {
    $profiles = Join-Path $env:APPDATA 'com.kesomannen.gale\valheim\profiles'
    $BepInExDir = Get-ChildItem $profiles -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'BepInEx' } |
        Where-Object {
            $hasCore = Test-Path (Join-Path $_ 'core\BepInEx.dll')
            $hasJotunn = [bool](Get-ChildItem (Join-Path $_ 'plugins') -Recurse -Filter Jotunn.dll -ErrorAction SilentlyContinue)
            $hasCore -and $hasJotunn
        } |
        Select-Object -First 1
    if (-not $BepInExDir) { throw 'No BepInEx folder with Jotunn found; pass -BepInExDir.' }
}
$jotunn = Get-ChildItem (Join-Path $BepInExDir 'plugins') -Recurse -Filter Jotunn.dll | Select-Object -First 1

if (-not $Csc) {
    $Csc = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio', 'C:\Program Files\Microsoft Visual Studio' -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Csc) { throw 'No Roslyn csc.exe found; install VS Build Tools or pass -Csc.' }
}

$refs = @(
    "$managed\mscorlib.dll", "$managed\netstandard.dll", "$managed\System.dll", "$managed\System.Core.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.UI.dll",
    "$managed\assembly_valheim.dll", "$managed\assembly_utils.dll", "$managed\assembly_guiutils.dll",
    (Join-Path $BepInExDir 'core\BepInEx.dll'), (Join-Path $BepInExDir 'core\0Harmony.dll'), $jotunn.FullName
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$rsp = Join-Path $dist 'build.rsp'
@('-nologo', '-target:library', "-out:$dist\PrefabGuard.dll", '-nostdlib+', '-langversion:7.3', '-debug-') +
    ($refs | ForEach-Object { "-r:`"$_`"" }) + "`"$root\src\PrefabGuard.cs`"" | Set-Content $rsp -Encoding ascii
& $Csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Compile failed ($LASTEXITCODE)" }
Write-Host "Built $dist\PrefabGuard.dll"

if ($Package) {
    $pkg = Join-Path $root 'package'
    foreach ($f in 'manifest.json', 'README.md', 'CHANGELOG.md', 'icon.png') {
        if (-not (Test-Path (Join-Path $pkg $f))) { throw "package\$f is missing" }
    }
    $version = (Get-Content (Join-Path $pkg 'manifest.json') -Raw | ConvertFrom-Json).version_number
    $stage = Join-Path $dist 'stage'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item "$dist\PrefabGuard.dll" $stage
    foreach ($f in 'manifest.json', 'README.md', 'CHANGELOG.md', 'icon.png') { Copy-Item (Join-Path $pkg $f) $stage }
    $zip = Join-Path $dist "Scooby_Drew902-PrefabGuard-$version.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path "$stage\*" -DestinationPath $zip
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zip"
}
