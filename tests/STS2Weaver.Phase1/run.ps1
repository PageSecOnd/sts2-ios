[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) {
    Join-Path $HOME '.dotnet\dotnet.exe'
} else {
    'dotnet'
}

$WeaverProject = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
$ModProject = Join-Path $PSScriptRoot 'FixtureMod\FixtureMod.csproj'
$UnsupportedModProject = Join-Path $PSScriptRoot 'FixtureUnsupportedMod\FixtureUnsupportedMod.csproj'
$RunnerProject = Join-Path $PSScriptRoot 'Runner\Runner.csproj'

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

# 显式使用官方源，避免用户级 NuGet.Config 没有源时出现不可复现的隐式失败。
foreach ($Project in @($WeaverProject, $ModProject, $UnsupportedModProject, $RunnerProject)) {
    Invoke-Dotnet restore $Project --source 'https://api.nuget.org/v3/index.json' --nologo
    Invoke-Dotnet build $Project -c $Configuration --no-restore --nologo
}

$TargetOutput = Join-Path $PSScriptRoot "FixtureTarget\bin\$Configuration\net9.0"
$ModOutput = Join-Path $PSScriptRoot "FixtureMod\bin\$Configuration\net9.0"
$UnsupportedModOutput = Join-Path $PSScriptRoot "FixtureUnsupportedMod\bin\$Configuration\net9.0"
$RunnerOutput = Join-Path $PSScriptRoot "Runner\bin\$Configuration\net9.0"
$WovenOutput = Join-Path $RunnerOutput 'woven'
New-Item -ItemType Directory -Force -Path $WovenOutput | Out-Null

$WovenAssembly = Join-Path $WovenOutput 'FixtureTarget.dll'
Invoke-Dotnet run --project $WeaverProject -c $Configuration --no-build -- `
    harmony `
    (Join-Path $TargetOutput 'FixtureTarget.dll') `
    (Join-Path $ModOutput 'FixtureMod.dll') `
    $WovenAssembly

Copy-Item (Join-Path $ModOutput 'FixtureMod.dll') $WovenOutput -Force

Invoke-Dotnet run --project $RunnerProject -c $Configuration --no-build -- $WovenAssembly

# 回归原有 manifest CLI，避免 Harmony 自动发现功能破坏现有 iOS 构建链。
$LegacyWovenAssembly = Join-Path $WovenOutput 'FixtureTarget.Legacy.dll'
Invoke-Dotnet run --project $WeaverProject -c $Configuration --no-build -- `
    (Join-Path $TargetOutput 'FixtureTarget.dll') `
    (Join-Path $ModOutput 'FixtureMod.dll') `
    (Join-Path $PSScriptRoot 'legacy-manifest.json') `
    $LegacyWovenAssembly
Invoke-Dotnet run --project $RunnerProject -c $Configuration --no-build -- $LegacyWovenAssembly

# 不兼容补丁必须响亮失败，而且不能留下部分织入产物。
$RejectedAssembly = Join-Path $WovenOutput 'ShouldNotExist.dll'
Remove-Item -LiteralPath $RejectedAssembly -Force -ErrorAction SilentlyContinue
$Diagnostic = (& $Dotnet run --project $WeaverProject -c $Configuration --no-build -- `
    harmony `
    (Join-Path $TargetOutput 'FixtureTarget.dll') `
    (Join-Path $UnsupportedModOutput 'FixtureUnsupportedMod.dll') `
    $RejectedAssembly 2>&1 | Out-String)
$RejectedExitCode = $LASTEXITCODE
if ($RejectedExitCode -eq 0) { throw 'Transpiler unexpectedly succeeded' }
if ($Diagnostic -notmatch 'Transpiler.*Phase 1 不支持') { throw "Transpiler diagnostic missing:`n$Diagnostic" }
if (Test-Path -LiteralPath $RejectedAssembly) { throw 'Rejected patch produced a partial output assembly' }
Write-Host '[PASS] 不兼容 Transpiler 被明确拒绝，且未生成部分产物'
