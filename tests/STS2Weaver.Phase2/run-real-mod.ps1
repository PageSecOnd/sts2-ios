[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) {
    Join-Path $HOME '.dotnet\dotnet.exe'
} else { 'dotnet' }

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Find-Sts2Install {
    $SteamRoot = ((Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath -replace '/', '\')
    $LibraryVdf = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
    $Libraries = @($SteamRoot)
    if (Test-Path $LibraryVdf) {
        $Libraries += [regex]::Matches((Get-Content $LibraryVdf -Raw), '"path"\s+"([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }

    foreach ($Library in $Libraries | Select-Object -Unique) {
        $Manifest = Join-Path $Library 'steamapps\appmanifest_2868840.acf'
        if (-not (Test-Path $Manifest)) { continue }
        $Text = Get-Content $Manifest -Raw
        $InstallDir = [regex]::Match($Text, '"installdir"\s+"([^"]+)"').Groups[1].Value
        if ($InstallDir) {
            return [pscustomobject]@{
                Game = Join-Path $Library "steamapps\common\$InstallDir"
                Workshop = Join-Path $Library 'steamapps\workshop\content\2868840'
            }
        }
    }
    throw '未在 Steam libraryfolders.vdf 指向的库中找到 AppID 2868840'
}

$Install = Find-Sts2Install
$GameData = Join-Path $Install.Game 'data_sts2_windows_x86_64'
$OriginalGame = Join-Path $GameData 'sts2.dll'
$BaseLib = Get-ChildItem $Install.Workshop -Recurse -File -Filter 'BaseLib.dll' |
    Where-Object { Test-Path (Join-Path $_.DirectoryName 'BaseLib.json') } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $BaseLib) { throw 'Workshop 中未找到 BaseLib.dll' }

$WeaverProject = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
$RunnerProject = Join-Path $PSScriptRoot 'RealModRunner\RealModRunner.csproj'
foreach ($Project in @($WeaverProject, $RunnerProject)) {
    Invoke-Dotnet restore $Project --source 'https://api.nuget.org/v3/index.json' --nologo
    Invoke-Dotnet build $Project -c $Configuration --no-restore --nologo
}

$Output = Join-Path $Root 'ios-export\.work\phase2'
New-Item -ItemType Directory -Force $Output | Out-Null
$WovenGame = Join-Path $Output 'sts2-baselib-getangle.dll'
$StaticBaseLib = Join-Path $Output 'BaseLib-static.dll'

Invoke-Dotnet run --project $WeaverProject -c $Configuration --no-build -- `
    harmony-one $OriginalGame $BaseLib `
    'BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch' 'GetAngle' `
    $WovenGame $StaticBaseLib

function Invoke-Probe([string]$Mode, [string]$GameDll, [string]$ModDll) {
    $OutputLines = & $Dotnet run --project $RunnerProject -c $Configuration --no-build -- `
        $Mode $GameDll $ModDll $GameData
    if ($LASTEXITCODE -ne 0) { throw "$Mode probe failed with exit code $LASTEXITCODE" }
    return @($OutputLines)[-1] | ConvertFrom-Json
}

$Original = Invoke-Probe 'original' $OriginalGame $BaseLib
$Harmony = Invoke-Probe 'harmony' $OriginalGame $BaseLib
$Woven = Invoke-Probe 'woven' $WovenGame $StaticBaseLib

for ($i = 0; $i -lt $Harmony.Results.Count; $i++) {
    if ($Harmony.Results[$i].Error -ne $Woven.Results[$i].Error) {
        throw "静态织入与 Harmony 异常行为不一致: case $i"
    }
    $Expected = [single]$Harmony.Results[$i].Result
    $Actual = [single]$Woven.Results[$i].Result
    if ([Math]::Abs($Expected - $Actual) -gt 0.0001) {
        throw "静态织入与 Harmony 不一致: case $i, harmony=$Expected, woven=$Actual"
    }
}
if (-not $Original.Results[1].Error -or $Harmony.Results[1].Error) {
    throw '候选补丁应将 handSize > 10 从原始异常路径改为有效结果'
}
if ([Math]::Abs([single]$Original.Results[0].Result - [single]$Harmony.Results[0].Result) -gt 0.0001) {
    throw 'Prefix 返回 true 时不应改变 handSize <= 10 的原始行为'
}

Write-Host '[PASS] 实际 BaseLib GetAngle：Harmony 运行时行为与 Cecil 静态织入行为一致'
$Original | ConvertTo-Json -Depth 5
$Harmony | ConvertTo-Json -Depth 5
$Woven | ConvertTo-Json -Depth 5
