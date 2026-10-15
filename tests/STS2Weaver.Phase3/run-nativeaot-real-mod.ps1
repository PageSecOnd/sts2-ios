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
    $Libraries = @($SteamRoot)
    $LibraryVdf = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
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

$Work = Join-Path $Root 'ios-export\.work\phase3'
$InputDir = Join-Path $Work 'nativeaot-input'
$PublishDir = Join-Path $Work 'nativeaot-publish'
Remove-Item -LiteralPath $InputDir, $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $InputDir, $PublishDir | Out-Null

$Weaver = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
$HostProject = Join-Path $PSScriptRoot 'NativeAotRealModHost\NativeAotRealModHost.csproj'
Invoke-Dotnet build $Weaver -c $Configuration --nologo
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- `
    harmony-one $OriginalGame $BaseLib `
    'BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch' 'GetAngle' `
    (Join-Path $InputDir 'sts2.dll') (Join-Path $InputDir 'BaseLib.dll')

Copy-Item -LiteralPath (Join-Path $GameData 'GodotSharp.dll') -Destination $InputDir -Force
if (Test-Path (Join-Path $InputDir '0Harmony.dll')) {
    throw 'NativeAOT 输入不应包含 Harmony 运行时程序集'
}

# 宿主没有 Harmony NuGet 引用，也不复制 0Harmony.dll。若可达闭包仍需要运行时 Harmony，ILC 会直接失败。
Remove-Item -LiteralPath $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Dotnet publish $HostProject -c $Configuration -r win-x64 --self-contained true `
    "-p:StaticModInputDir=$InputDir" -o $PublishDir --nologo

$Exe = Join-Path $PublishDir 'NativeAotRealModHost.exe'
if (-not (Test-Path $Exe)) { throw "NativeAOT 未生成原生程序: $Exe" }
$Output = & $Exe
if ($LASTEXITCODE -ne 0) { throw "NativeAOT 原生程序退出码: $LASTEXITCODE" }
$Output | Write-Host
if ($Output -notcontains '[PASS] NativeAOT executed the statically woven real BaseLib GetAngle Prefix without Harmony runtime patching') {
    throw '原生程序没有输出成功标记'
}
Write-Host "[PASS] 真实 BaseLib Prefix 已参与 Windows NativeAOT 编译并在无 JIT 进程中执行"
