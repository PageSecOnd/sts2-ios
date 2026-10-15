[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) {
    Join-Path $HOME '.dotnet\dotnet.exe'
} else { 'dotnet' }
$Project = Join-Path $PSScriptRoot 'AotSmoke\AotSmoke.csproj'
$Output = Join-Path $PSScriptRoot "AotSmoke\bin\$Configuration\native"

& $Dotnet restore $Project -r win-x64 --source 'https://api.nuget.org/v3/index.json' --nologo
if ($LASTEXITCODE -ne 0) { throw "NativeAOT restore failed: $LASTEXITCODE" }
& $Dotnet publish $Project -c $Configuration -r win-x64 --self-contained true --no-restore -o $Output --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Windows NativeAOT publish 未通过；通常需要 Visual Studio 2022 C++ Desktop Build Tools。'
    exit $LASTEXITCODE
}

& (Join-Path $Output 'AotSmoke.exe')
if ($LASTEXITCODE -ne 0) { throw "NativeAOT executable failed: $LASTEXITCODE" }
