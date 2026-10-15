[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) { Join-Path $HOME '.dotnet\dotnet.exe' } else { 'dotnet' }

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'prepare-real-baselib-input.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "真实 BaseLib iOS 托管输入准备失败: $LASTEXITCODE" }

$InputDir = Join-Path $Root 'ios-export\.work\phase4\real-baselib-input'
$Publish = Join-Path $Root 'ios-export\.work\phase4\real-baselib-nativeaot'
Remove-Item $Publish -Recurse -Force -ErrorAction SilentlyContinue
$Project = Join-Path $PSScriptRoot 'NativeAotRealRegistryHost\NativeAotRealRegistryHost.csproj'
& $Dotnet publish $Project -c $Configuration -r win-x64 --self-contained true `
    "-p:StaticModInputDir=$InputDir" -o $Publish --nologo
if ($LASTEXITCODE -ne 0) { throw "真实 BaseLib 注册宿主 NativeAOT publish 失败: $LASTEXITCODE" }

$Output = & (Join-Path $Publish 'NativeAotRealRegistryHost.exe')
if ($LASTEXITCODE -ne 0 -or $Output -notmatch 'real BaseLib registered once') {
    throw "真实 BaseLib 注册宿主执行失败:`n$($Output -join "`n")"
}
$Output | Write-Host
