# Phase 3：NativeAOT 静态 Mod 运行验证

这些测试不向仓库复制游戏或 Mod 二进制；真实程序集只进入被忽略的
`ios-export/.work/phase3`。

```powershell
# 无 Mod 的最小 NativeAOT 环境验证
pwsh tests/STS2Weaver.Phase2/run-aot-smoke.ps1

# 真实 BaseLib GetAngle Prefix：只读定位 Steam/Workshop、织入、publish、运行
pwsh tests/STS2Weaver.Phase3/run-nativeaot-real-mod.ps1

# 依赖、初始化、静态构造、泛型及不兼容诊断矩阵
pwsh tests/STS2Weaver.Phase3/run-compatibility-matrix.ps1
```

真实 Mod 宿主在源码层面对织入后的 `sts2.dll`、提升 hook 可见性的 `BaseLib.dll`
和 `GodotSharp.dll` 建立静态引用。工程没有 Harmony NuGet 引用，输入目录也没有
`0Harmony.dll`；运行时同时断言 `RuntimeFeature.IsDynamicCodeSupported` 和
`RuntimeFeature.IsDynamicCodeCompiled` 都为 `false`。

`BaseLibStaticAdapter` 是 Phase 3 的编译期选择性适配入口，不是 BaseLib 原始
`BaseLibMain.Initialize`。它证明选中 Mod assembly 已进入 AOT 闭包且注册入口得到执行；
BaseLib 的完整配置、Godot 内容和资源初始化仍未验证。
