# Phase 2：真实 BaseLib 补丁验证

`run-real-mod.ps1` 自动解析 Steam `libraryfolders.vdf`，只读定位正版游戏与 Workshop BaseLib，
然后选择 BaseLib 的 `HandPosHelper.GetAngle` Prefix 做三路对照：

1. 未修改的游戏程序集；
2. 未修改游戏程序集 + Harmony 2.4.2 运行时补丁；
3. Cecil 静态织入的游戏副本 + 仅提升所选 hook 可见性的 BaseLib 副本。

所有修改产物都写入已忽略的 `ios-export/.work/phase2/`，不会覆盖或跟踪 Steam 文件。

```powershell
pwsh tests/STS2Weaver.Phase2/run-real-mod.ps1
```

Windows NativeAOT 独立冒烟测试：

```powershell
pwsh tests/STS2Weaver.Phase2/run-aot-smoke.ps1
```

这只验证当前 Windows 工具链，不等价于 iOS NativeAOT。缺少 Visual Studio C++ Build Tools 时会明确失败。
