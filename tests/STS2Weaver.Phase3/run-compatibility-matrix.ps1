[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) { Join-Path $HOME '.dotnet\dotnet.exe' } else { 'dotnet' }
$Work = Join-Path $Root 'ios-export\.work\phase3\matrix'
New-Item -ItemType Directory -Force $Work | Out-Null

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $Text = (& $Action 2>&1 | Out-String)
    $Code = $LASTEXITCODE
    if ($Code -eq 0) { throw "$Name unexpectedly succeeded" }
    if ($Text -notmatch $Pattern) { throw "$Name missing diagnostic '$Pattern':`n$Text" }
    Write-Host "[PASS] $Name 被明确拒绝"
}

$Weaver = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
$TargetProject = Join-Path $Root 'tests\STS2Weaver.Phase1\FixtureTarget\FixtureTarget.csproj'
$DependentProject = Join-Path $PSScriptRoot 'MatrixDependentMod\MatrixDependentMod.csproj'
$DynamicProject = Join-Path $PSScriptRoot 'MatrixDynamicTargetMod\MatrixDynamicTargetMod.csproj'
$RiskProject = Join-Path $PSScriptRoot 'MatrixRiskMod\MatrixRiskMod.csproj'
foreach ($Project in @($Weaver, $TargetProject, $DependentProject, $DynamicProject, $RiskProject)) {
    Invoke-Dotnet build $Project -c $Configuration --nologo
}

$TargetDir = Join-Path $Root "tests\STS2Weaver.Phase1\FixtureTarget\bin\$Configuration\net9.0"
$DependentDir = Join-Path $PSScriptRoot "MatrixDependentMod\bin\$Configuration\net9.0"
$DynamicDir = Join-Path $PSScriptRoot "MatrixDynamicTargetMod\bin\$Configuration\net9.0"
$RiskDir = Join-Path $PSScriptRoot "MatrixRiskMod\bin\$Configuration\net9.0"
$DependentDll = Join-Path $DependentDir 'MatrixDependentMod.dll'

Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- `
    preflight-aot $DependentDll $DependentDir (Join-Path $Work 'dependency-ok.json')

$MissingDir = Join-Path $Work 'missing-dependency'
Remove-Item $MissingDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $MissingDir | Out-Null
Copy-Item $DependentDll, (Join-Path $DependentDir 'FixtureTarget.dll') $MissingDir
Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- `
        preflight-aot (Join-Path $MissingDir 'MatrixDependentMod.dll') $MissingDir (Join-Path $Work 'missing-dependency.json')
} 'AOT1001.*MatrixDependency' '缺失程序集依赖'

Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- `
        harmony (Join-Path $TargetDir 'FixtureTarget.dll') `
        (Join-Path $DynamicDir 'MatrixDynamicTargetMod.dll') (Join-Path $Work 'dynamic-should-not-exist.dll')
} '动态 TargetMethod/TargetMethods' '动态 TargetMethod'

Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- `
        preflight-aot (Join-Path $RiskDir 'MatrixRiskMod.dll') $RiskDir (Join-Path $Work 'runtime-risks.json')
} '(?s)(?=.*AOT2001.*Reflection.Emit)(?=.*AOT2002.*运行时程序集加载)(?=.*AOT2003.*Harmony)' 'Reflection.Emit、Assembly.Load 与 Harmony PatchAll'

$InputDir = Join-Path $Work 'dependency-aot-input'
$PublishDir = Join-Path $Work 'dependency-aot-publish'
Remove-Item $InputDir, $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $InputDir | Out-Null
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- `
    harmony (Join-Path $TargetDir 'FixtureTarget.dll') $DependentDll (Join-Path $InputDir 'FixtureTarget.dll')
Copy-Item $DependentDll, (Join-Path $DependentDir 'MatrixDependency.dll') $InputDir

$HostProject = Join-Path $PSScriptRoot 'NativeAotDependencyHost\NativeAotDependencyHost.csproj'
Invoke-Dotnet publish $HostProject -c $Configuration -r win-x64 --self-contained true `
    "-p:StaticModInputDir=$InputDir" -o $PublishDir --nologo
$Output = & (Join-Path $PublishDir 'NativeAotDependencyHost.exe')
if ($LASTEXITCODE -ne 0 -or $Output -notmatch 'NativeAOT static mod dependency executed') {
    throw "依赖 Mod NativeAOT 运行失败:`n$($Output -join "`n")"
}
$Output | Write-Host
Write-Host '[PASS] Phase 3 兼容性矩阵自动化测试完成'
