[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) { Join-Path $HOME '.dotnet\dotnet.exe' } else { 'dotnet' }

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}
function Find-Sts2Install {
    $SteamRoot = ((Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath -replace '/', '\')
    $Libraries = @($SteamRoot)
    $Vdf = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path $Vdf) {
        $Libraries += [regex]::Matches((Get-Content $Vdf -Raw), '"path"\s+"([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($Library in $Libraries | Select-Object -Unique) {
        $Acf = Join-Path $Library 'steamapps\appmanifest_2868840.acf'
        if (-not (Test-Path $Acf)) { continue }
        $InstallDir = [regex]::Match((Get-Content $Acf -Raw), '"installdir"\s+"([^"]+)"').Groups[1].Value
        if ($InstallDir) {
            return [pscustomobject]@{
                Game = Join-Path $Library "steamapps\common\$InstallDir"
                Workshop = Join-Path $Library 'steamapps\workshop\content\2868840'
            }
        }
    }
    throw '未找到 Steam AppID 2868840'
}

$Install = Find-Sts2Install
$GameData = Join-Path $Install.Game 'data_sts2_windows_x86_64'
$Game = Join-Path $GameData 'sts2.dll'
$BaseLib = Get-ChildItem $Install.Workshop -Recurse -File -Filter BaseLib.dll |
    Where-Object { Test-Path (Join-Path $_.DirectoryName 'BaseLib.json') } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $BaseLib) { throw '未找到已安装的 Workshop BaseLib.dll' }

$Output = Join-Path $Root 'ios-export\.work\phase4\real-baselib-input'
Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $Output 'deps') | Out-Null
$Weaver = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
Invoke-Dotnet build $Weaver -c $Configuration --nologo

$Patched = Join-Path $Output 'sts2.selected-hook.dll'
$StaticBaseLib = Join-Path $Output 'deps\BaseLib.dll'
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- harmony-one `
    $Game $BaseLib 'BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch' 'GetAngle' `
    $Patched $StaticBaseLib

$Identity = [Reflection.AssemblyName]::GetAssemblyName($Patched)
$Manifest = [ordered]@{
    schemaVersion = 1
    target = [ordered]@{
        assemblyName = $Identity.Name
        assemblyVersion = $Identity.Version.ToString()
        sha256 = (Get-FileHash $Patched -Algorithm SHA256).Hash
    }
    mods = @([ordered]@{
        id = 'BaseLib.GetAngle.SelectedPatch'
        assembly = 'BaseLib.dll'
        assemblyVersion = ([Reflection.AssemblyName]::GetAssemblyName($StaticBaseLib)).Version.ToString()
        dependencies = @()
        rootTypes = @('BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch')
    })
}
$ManifestPath = Join-Path $Output 'static-mods.resolved.json'
$Manifest | ConvertTo-Json -Depth 8 | Set-Content $ManifestPath -Encoding utf8NoBOM
$Registry = Join-Path $Output 'deps\STS2StaticModRegistry.dll'
$Roots = Join-Path $Output 'static-mod-roots.xml'
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
    $ManifestPath $Patched (Join-Path $Output 'deps') $Registry $Roots
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- inject-registry `
    $Patched $Registry (Join-Path $Output 'sts2.dll') $GameData
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- verify-static-input `
    $ManifestPath (Join-Path $Output 'deps') $GameData (Join-Path $Output 'dependency-report.json')
Copy-Item (Join-Path $GameData 'GodotSharp.dll') (Join-Path $Output 'deps\GodotSharp.dll')

$MacAssembly = Get-ChildItem $Install.Game -Recurse -File -Filter sts2.dll -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'data_sts2_macos_arm64' } | Select-Object -First 1
if ($MacAssembly) {
    $WindowsHash = (Get-FileHash $Game -Algorithm SHA256).Hash
    $MacHash = (Get-FileHash $MacAssembly.FullName -Algorithm SHA256).Hash
    Write-Host "[INFO] Windows/macOS sts2.dll hash equal: $($WindowsHash -eq $MacHash)"
} else {
    Write-Host '[INFO] 本机没有 macOS 游戏程序集；平台间 DLL 身份差异尚未验证'
}

Write-Host "[PASS] 已从真实 BaseLib 构造静态注册 NativeAOT 托管输入: $Output"
Write-Host '[INFO] 这是 Windows 托管输入验证，不等于 ios-arm64 编译成功'
