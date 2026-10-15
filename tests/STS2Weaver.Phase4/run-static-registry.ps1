[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Dotnet = if (Test-Path (Join-Path $HOME '.dotnet\dotnet.exe')) { Join-Path $HOME '.dotnet\dotnet.exe' } else { 'dotnet' }
$Work = Join-Path $Root 'ios-export\.work\phase4\registry-tests'
$InputDir = Join-Path $Work 'registry-input'
$Publish = Join-Path $Work 'registry-publish'
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $InputDir | Out-Null

function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}
function Write-Manifest([string]$Path, [object]$Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $Text = (& $Action 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { throw "$Name unexpectedly succeeded" }
    if ($Text -notmatch $Pattern) { throw "$Name missing diagnostic '$Pattern':`n$Text" }
    Write-Host "[PASS] $Name 被明确拒绝"
}

$Weaver = Join-Path $Root 'src\STS2Weaver\STS2Weaver.csproj'
$TargetProject = Join-Path $Root 'tests\STS2Weaver.Phase1\FixtureTarget\FixtureTarget.csproj'
$DependentProject = Join-Path $Root 'tests\STS2Weaver.Phase3\MatrixDependentMod\MatrixDependentMod.csproj'
foreach ($Project in @($Weaver, $TargetProject, $DependentProject)) {
    Invoke-Dotnet build $Project -c $Configuration --nologo
}

$Target = Join-Path $Root "tests\STS2Weaver.Phase1\FixtureTarget\bin\$Configuration\net9.0\FixtureTarget.dll"
$ModDir = Join-Path $Root "tests\STS2Weaver.Phase3\MatrixDependentMod\bin\$Configuration\net9.0"
Copy-Item $Target (Join-Path $InputDir 'FixtureTarget.original.dll')
Copy-Item (Join-Path $ModDir 'MatrixDependentMod.dll'), (Join-Path $ModDir 'MatrixDependency.dll') $InputDir

$Dependency = [ordered]@{
    id = 'dependency'; assembly = 'MatrixDependency.dll'; dependencies = @()
    initializer = [ordered]@{ type = 'STS2Weaver.Phase3.MatrixDependency.DependencyModEntry'; method = 'Initialize' }
    rootTypes = @('STS2Weaver.Phase3.MatrixDependency.DependencyApi')
}
$Dependent = [ordered]@{
    id = 'dependent'; assembly = 'MatrixDependentMod.dll'; dependencies = @('dependency')
    initializer = [ordered]@{ type = 'STS2Weaver.Phase3.MatrixDependentMod.ModEntry'; method = 'Initialize' }
    rootTypes = @('STS2Weaver.Phase3.MatrixDependentMod.TrimOnlyType')
}

# 无 Mod 输入：生成空注册表，不修改目标；对应 ios-export 未配置 manifest 的兼容路径。
$EmptyManifest = Join-Path $Work 'empty.json'
Write-Manifest $EmptyManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@() })
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
    $EmptyManifest $Target $InputDir (Join-Path $Work 'EmptyRegistry.dll') (Join-Path $Work 'empty-roots.xml')

# 单 Mod 输入。
$SingleManifest = Join-Path $Work 'single.json'
Write-Manifest $SingleManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@($Dependency) })
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
    $SingleManifest $Target $InputDir (Join-Path $Work 'SingleRegistry.dll') (Join-Path $Work 'single-roots.xml')

# 故意逆序声明；生成器必须按依赖拓扑改为 dependency -> dependent。
$Manifest = Join-Path $Work 'two-mods.json'
Write-Manifest $Manifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@($Dependent,$Dependency) })
$Registry = Join-Path $InputDir 'STS2StaticModRegistry.dll'
$Roots = Join-Path $InputDir 'static-mod-roots.xml'
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
    $Manifest $Target $InputDir $Registry $Roots
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- verify-static-input `
    $Manifest $InputDir $InputDir (Join-Path $Work 'input-ok.json')
Invoke-Dotnet run --project $Weaver -c $Configuration --no-build -- inject-registry `
    $Target $Registry (Join-Path $InputDir 'FixtureTarget.dll') $InputDir

Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- inject-registry `
        (Join-Path $InputDir 'FixtureTarget.dll') $Registry (Join-Path $Work 'duplicate.dll') $InputDir
} '已包含静态 Mod 注册调用' '重复注册注入'

$MissingManifest = Join-Path $Work 'missing.json'
$Missing = [ordered]@{} + $Dependent
$Missing.dependencies = @('not-installed')
Write-Manifest $MissingManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@($Missing) })
Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
        $MissingManifest $Target $InputDir (Join-Path $Work 'missing.dll') (Join-Path $Work 'missing.xml')
} '缺少依赖声明: not-installed' '缺失 Mod 依赖'

$MissingBinaryDir = Join-Path $Work 'missing-binary'
New-Item -ItemType Directory -Force $MissingBinaryDir | Out-Null
Copy-Item (Join-Path $InputDir 'MatrixDependentMod.dll') $MissingBinaryDir
$MissingBinaryMod = [ordered]@{} + $Dependent; $MissingBinaryMod.dependencies=@()
$MissingBinaryManifest = Join-Path $Work 'missing-binary.json'
Write-Manifest $MissingBinaryManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@($MissingBinaryMod) })
Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- verify-static-input `
        $MissingBinaryManifest $MissingBinaryDir $MissingBinaryDir (Join-Path $Work 'missing-binary-report.json')
} 'STATIC1001.*MatrixDependency' '缺失程序集文件'

$CycleA = [ordered]@{} + $Dependency; $CycleA.dependencies=@('dependent')
$CycleManifest = Join-Path $Work 'cycle.json'
Write-Manifest $CycleManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget'}; mods=@($CycleA,$Dependent) })
Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
        $CycleManifest $Target $InputDir (Join-Path $Work 'cycle.dll') (Join-Path $Work 'cycle.xml')
} '依赖存在环' '循环 Mod 依赖'

$MismatchManifest = Join-Path $Work 'mismatch.json'
Write-Manifest $MismatchManifest ([ordered]@{ schemaVersion=1; target=[ordered]@{assemblyName='FixtureTarget';assemblyVersion='99.0.0.0'}; mods=@() })
Assert-Fails {
    & $Dotnet run --project $Weaver -c $Configuration --no-build -- static-registry `
        $MismatchManifest $Target $InputDir (Join-Path $Work 'mismatch.dll') (Join-Path $Work 'mismatch.xml')
} '目标版本不匹配' '游戏版本与构建配置不一致'

$HostProject = Join-Path $PSScriptRoot 'NativeAotRegistryHost\NativeAotRegistryHost.csproj'
Invoke-Dotnet publish $HostProject -c $Configuration -r win-x64 --self-contained true `
    "-p:StaticModInputDir=$InputDir" -o $Publish --nologo
$Output = & (Join-Path $Publish 'NativeAotRegistryHost.exe')
if ($LASTEXITCODE -ne 0 -or $Output -notmatch 'NativeAOT generated registry') {
    throw "静态注册表 NativeAOT 运行失败:`n$($Output -join "`n")"
}
$Output | Write-Host
Write-Host '[PASS] Phase 4 静态 Mod 注册、顺序、幂等、版本与 trimming 测试完成'
