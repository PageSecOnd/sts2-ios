# Phase 3：NativeAOT 静态 Mod 运行验证

日期：2026-10-15。测试对象仍为用户本机正版游戏 v0.107.1、BaseLib v3.4.7、
Harmony v2.4.2、.NET SDK 9.0.319。游戏和 Mod 二进制没有进入 Git。

## 工具链修复

Phase 2 的 `PublishAot` 失败是 Windows 原生链接器缺失，不是 C# 或 Weaver 不兼容。
本机安装了 Visual Studio Build Tools 2022 17.14.41 的
`Microsoft.VisualStudio.Workload.VCTools`（默认/推荐组件），得到：

- MSVC 14.44.35207；
- Windows SDK 10.0.26100.0；
- x64/x86/arm64 原生工具；
- .NET 9.0.319 / ILCompiler。

最小 `AotSmoke` 随后成功 publish 并运行，原生进程确认动态代码支持和动态代码编译均关闭。

## 真实 BaseLib 补丁闭环

`run-nativeaot-real-mod.ps1` 每次都执行以下流程：

1. 从 Steam `libraryfolders.vdf` 只读定位游戏与 Workshop BaseLib；
2. 把输出写入 `ios-export/.work/phase3/nativeaot-input`；
3. 用 `harmony-one` 将真实私有
   `BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch.GetAngle` Prefix 织入游戏副本，
   并只在 Mod 副本提升选中类型和方法的可见性；
4. 以编译期引用把织入的 `sts2.dll`、真实 `BaseLib.dll` 副本和 `GodotSharp.dll`
   交给 ILC；
5. 不引用 Harmony NuGet 包、不复制 `0Harmony.dll`，发布 `win-x64` NativeAOT；
6. 运行原生 EXE，直接调用 `HandPosHelper.GetAngle`。

实际原生结果：

| 输入 | Harmony 2.4.2（Phase 2） | NativeAOT 静态织入 |
|---|---:|---:|
| `(10, 0)` | -15 | -15 |
| `(12, 0)` | -14.656751 | -14.656751 |
| `(12, 5)` | -1.3620203 | -1.3620203 |
| `(14, 13)` | 13.370044 | 13.370044 |

这验证了“选中真实 hook 已织入”“真实 Mod 程序集进入原生可达闭包”“原生程序实际执行
hook”三件事。没有使用 JIT、`Assembly.Load*`、Reflection.Emit 或运行时 `PatchAll`。

## 初始化边界

真实 BaseLib 的 `[ModInitializer]` 指向 `BaseLib.BaseLibMain.Initialize`。实际 IL/源码审计表明，
它除了注册 Godot 脚本、配置和保存扩展，还进入 `MainHarmony.TryPatchAll`。整程序集扫描发现
45 个 Reflection.Emit、15 个运行时 Harmony、7 个运行时泛型实例化、13 个反射激活和
3 个 P/Invoke 风险点。因此本阶段没有谎称完整 BaseLib 初始化可在 iOS 上执行。

原生真实 Mod 宿主使用固定 `Action[]` 注册表执行 `BaseLibStaticAdapter.Initialize`：该入口以
`typeof(HandPosHelperGetPositionPatch)` 和 `RuntimeHelpers.RunClassConstructor` 建立真实 hook 的
显式可达性边。这是选择性 AOT 适配入口，不是原始 `BaseLibMain.Initialize`。完整 BaseLib 内容
注册和资源加载仍未验证，原始入口需要拆分为“安全注册”和“跳过 Harmony 动态补丁”两段后才能接入。

## 构建前 AOT 诊断

Weaver 新增：

```text
STS2Weaver preflight-aot <mod.dll> <dependency-dir> <report.json>
```

它会在 publish 前拒绝：

- `AOT1001`：缺失的非框架程序集依赖；
- `AOT2001`：Reflection.Emit / DynamicMethod；
- `AOT2002`：Assembly.Load / AssemblyLoadContext.Load；
- `AOT2003`：仍可见的 Harmony Patch/PatchAll 调用。

运行时泛型构造和 `Activator.CreateInstance` 分别报告 `AOT3001/AOT3002` 警告。`0Harmony`
引用可仅作为构建期特性元数据存在；如果方法体调用 Harmony patch API，仍由 AOT2003 拒绝。

## 兼容性矩阵

| 场景 | CoreCLR | NativeAOT 编译 | NativeAOT 运行 | iOS | 结论 |
|---|---|---|---|---|---|
| 无 Mod | 不适用 | 通过 | 通过 | 未验证 | `AotSmoke` |
| 普通 Prefix | 通过 | 通过 | 通过 | 未验证 | Phase 1 + 真实 BaseLib |
| 普通 Postfix | 通过 | 通过 | 通过 | 未验证 | 依赖夹具 `1+2+5=8` |
| 同目标 Prefix + Postfix | 通过 | 未单独发布 | 未单独运行 | 未验证 | Phase 1 `GetValue` |
| Mod 依赖另一个 Mod | 通过 | 通过 | 通过 | 未验证 | 显式程序集引用 |
| 私有 hook | 通过 | 通过 | 通过 | 未验证 | `harmony-one` 输出副本提升可见性 |
| 缺失程序集依赖 | 不执行 | 编译前拒绝 | 不执行 | 未验证 | `AOT1001` |
| 动态 TargetMethod | 不执行 | 编译前拒绝 | 不执行 | 未验证 | 明确诊断 |
| Transpiler | 不执行 | 编译前拒绝 | 不执行 | 未验证 | Phase 1 回归 |
| Reflection.Emit | 不执行 | 编译前拒绝 | 不执行 | 未验证 | `AOT2001` |
| Harmony PatchAll | 不执行 | 编译前拒绝 | 不执行 | 未验证 | `AOT2003` |

依赖夹具同时验证编译期 Mod 初始化表、依赖程序集静态构造函数只执行一次，以及显式
`Identity<int>` 泛型实例化。反射发现仍不能作为默认机制；需要生成直接引用，或对确有必要的
反射类型使用经过审计的 trimming root / descriptor。

## iOS 管线接入

`config.example.sh` 新增可选 `STS2_STATIC_MOD_*` 设置。未配置时 `build-ios.sh` 明确走原无 Mod
路径。配置后，脚本在 `.work` 游戏副本上继续执行 `harmony-one`，把输出 Mod 与显式依赖复制到
`prebuilt/deps`，再由现有 Godot thin shim/ILC 编译。选中 hook 由织入调用自然可达；额外程序集
可通过 `STS2_STATIC_MOD_ROOT_ASSEMBLIES` 显式 root。

Windows 本阶段没有 iOS SDK、Godot iOS 模板或 Xcode，因此没有执行 `ios-arm64` publish、签名
或真机验证。可选 shell 分支也尚需在 macOS 上实际执行，不能把 Windows NativeAOT 结果等同于 iOS。

## 尚未解决

- 完整 BaseLib 仍含大量 Transpiler、动态目标、字段注入、Getter/Setter/Async 目标和 Emit；
- 原始 `BaseLibMain.Initialize` 尚未拆分执行，内容注册和 PCK 资源加载未验证；
- 当前 iOS 可选配置先支持一个经过选择的 hook，多 Mod 拓扑/冲突管理尚未接入；
- iOS trimming、Godot 脚本发现、P/Invoke 分支和 `ios-arm64` ILC 尚未实测；
- Windows 原生验证只证明 .NET NativeAOT 可达闭包，不证明 Apple 平台 ABI、签名或资源行为。
