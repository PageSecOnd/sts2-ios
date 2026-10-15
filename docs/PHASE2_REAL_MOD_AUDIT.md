# Phase 2：真实游戏与 Mod 静态移植审计

审计日期：2026-10-15。所有 Steam/Workshop 文件均只读；织入产物只写入已忽略的
`ios-export/.work/phase2/`。本报告不包含游戏或 Mod 二进制、PCK、资源或反编译 IL。

## 状态标记

- **已由自动化测试验证**：实际执行并断言结果。
- **仅完成静态分析**：来自实际程序集元数据/IL 或指定公开源码，但未执行游戏流程。
- **尚未验证**：需要 iOS NativeAOT、Godot 场景树或真机。
- **已发现不兼容**：当前转换器明确拒绝或 NativeAOT 明确不提供该能力。

## 1. 游戏与安装信息

**仅完成静态分析**

| 项目 | 实测值 |
|---|---|
| Steam AppID | `2868840` |
| Steam build ID | `23811903` |
| 游戏版本 | `v0.107.1` |
| 游戏提交 | `59260271157f76a2896f0eab5bc6ea1245d8b314`（ProductVersion） |
| 主程序集 | `data_sts2_windows_x86_64/sts2.dll` |
| 程序集身份 | `sts2, Version=0.1.0.0` |
| `sts2.dll` 大小 | `9,364,480` bytes |
| `sts2.dll` SHA-256 | `A1F9E653F1E28E4076558FEE1E60D218619CB7E057B887C6417F62C62C6D7A52` |
| .NET | 9.0 程序集集 |
| GodotSharp | 4.5.1.0 |
| Harmony | 2.4.2.0 |

`sts2.dll` 的主要非 BCL 引用：`0Harmony 2.4.2`、`GodotSharp 4.5.1`、
`Sentry 5.0`、`SharpGen.Runtime 2.2`、`SmartFormat 3.0`、`Steamworks.NET 1.0`、
`Vortice.DXGI 3.6.2`。iOS 构建仍需按既有流程替换/排除 Windows 原生依赖。

Steam `libraryfolders.vdf` 中只有一个库。本次找到 21 个 Workshop item、24 份 Mod manifest：

| Workshop ID | Mod | 版本 | 形态 |
|---|---|---|---|
| 3737335127 | BaseLib | v3.4.7 | DLL + PCK |
| 3737336234 | Minty Spire 2 | v1.2.0 | DLL + PCK |
| 3747492505 | Watcher | 1.5.12 | DLL + PCK |
| 3747528152 | Intent Graph | 1.6.0 | 多 DLL + PCK |
| 3747530215 | BetterSpire2 | v1.83.10 | DLL |
| 3747557003 | ModConfig | 0.2.3 | DLL + PCK |
| 3747557049 | RouteSuggest | 1.13.0 | DLL + PCK |
| 3747557283 | DamageMeter | 1.14.7 | DLL + PCK |
| 3747557762 | Rewind | 0.26.19 | DLL + PCK |
| 3747591649 | silentSkin | 0.7.1 | PCK |
| 3747602295 | RitsuLib | 0.6.7 | 多版本 DLL |
| 3747611326 | neowSkin | 0.0.1 | PCK |
| 3747626664 | RegentCardsAnimeRework | v1.0 | DLL + PCK |
| 3747716772 | Calypso's Happy Hour | v1.3.2 | 多版本 DLL + PCK |
| 3747942752 | Mechaneko Defect Skin | 1.1.2 | DLL + PCK |
| 3747952763 | ATA IronClad | 0.1.12 | DLL + PCK |
| 3748331824 | Merchant2CuteII | v1.5.3 | DLL + PCK |
| 3748603697 | Mesugaki Regent | 0.1.2 | DLL + PCK |
| 3749627968 | NecrobinderCardPortraits | 1.4.0 | DLL + PCK |
| 3749627968 | NecrobinderOstyAnimMod | 1.4.0 | DLL |
| 3749627968 | OrchisNecrobinderSkinMod | 1.0.0 | DLL + PCK |
| 3749627968 | RestSiteFondleCursorMod | 1.4.0 | DLL + PCK |
| 3750220799 | LAN Multiplayer Reforged | 1.0.0 | DLL + PCK |
| 3751434747 | Hermit's Mod Doctor | v0.2.0 | DLL |

本机 **没有安装 QuickRestart**。因此不能把 GitHub Release 当作本机版本；其结果单独标为公开源码分析。

## 2. BaseLib v3.4.7

**仅完成静态分析（全量）**；**已由自动化测试验证（所选一个补丁）**

| 项目 | 实测值 |
|---|---|
| Workshop DLL 身份 | `BaseLib, Version=3.4.7.0` |
| ProductVersion | `3.4.7+255b1531a5cb6ba7e4e4b76dc3dd2afbc4a42b23` |
| SHA-256 | `55863CA6ADC30A61A7544874E157A519FFE148CCF2B18025947609C4BFF813CE` |
| 初始化器 | `BaseLib.BaseLibMain.Initialize` |
| 直接依赖 | sts2、GodotSharp、0Harmony、SmartFormat、Steamworks.NET |

程序集元数据共识别 338 个 Harmony hook：146 Prefix、145 Postfix、47 Transpiler、
0 Finalizer、20 个动态目标。按当前转换器的保守判定：

| 判定 | 数量 | 含义 |
|---|---:|---|
| `supported` | 18 | 声明式目标、支持的参数、hook 与类型已公开；仅代表元数据可转换 |
| `requires-hook-promotion` | 170 | 语义可转换，但需在独立 Mod 副本提升 hook/嵌套类型可见性 |
| `unsupported` | 150 | 当前必须拒绝 |

主要不兼容原因：87 个 Getter、47 个 Transpiler、20 个动态 TargetMethod(s)、
6 个 Async 目标、4 个 Setter、若干私有字段注入参数和一个重载未消歧目标。

完整逐 hook 矩阵见 [`phase2-baselib-patch-matrix.csv`](phase2-baselib-patch-matrix.csv)，包含补丁类、
hook、目标类型/方法、Prefix/Postfix/Transpiler/Finalizer、动态目标、参数、特殊参数、
解析后的游戏方法、兼容判定和拒绝原因。

### 初始化与 AOT 风险

`BaseLibMain.Initialize` 会注册 Godot 脚本、配置和保存扩展，随后调用
`MainHarmony.TryPatchAll(assembly)`。因此“静态织入成功”不等于 BaseLib 初始化完成；
静态版必须保留必要注册逻辑，同时移除/旁路运行时 Harmony 调用。

保守 IL 扫描得到的风险调用点：Reflection.Emit 45、运行时 Harmony 15、运行时泛型实例化 7、
反射激活 13、反射元数据访问 304、P/Invoke 3。P/Invoke 指向 Linux `libdl.so.2` 且源码有
OS 判断，但 iOS AOT 仍需确保该分支被裁剪或替换。Reflection.Emit/运行时 Harmony 在 iOS
不可执行；反射、激活和动态泛型需要显式保根与实例化审计。

## 3. QuickRestart v2.0.0（公开源码当前提交）

**仅完成静态分析**；本机没有 Workshop 二进制。

分析源码提交：`25b0ef2e2ab4c59ad6c5ac02a1243c034ed9e3bb`。项目依赖 BaseLib >= 3.4.5，
初始化器 `QuickRestart.QuickRestartCode.MainFile.Initialize` 先注册 BaseLib 配置，再执行
`Harmony.PatchAll()`。从源码使用本机游戏引用构建成功（4 个 nullable warning、0 error）。

| 补丁类 / hook | 目标 | 类型 | 特殊状态/参数 | 当前结果 |
|---|---|---|---|---|
| `Keybind.Postfix` | `NGame._Input(InputEvent)` | Postfix（方法名约定） | 输入事件；依赖 Godot 输入/时间和配置 | 需提升私有 hook；未做场景行为验证 |
| `PauseMenuButtonPatch.InitRestartButton` | `NPauseMenu._Ready()` | Postfix | `__instance` + `____buttonContainer/settingsButton/giveUpButton` 私有字段注入 | **已发现不兼容**：尚无字段注入转换 |
| `OnPauseMenuOpen.Postfix` | `NPauseMenu.Initialize(IRunState)` | Postfix | `__instance`；依赖场景节点和 RunManager | 元数据支持；尚未做场景行为验证 |
| `ButtonDimmingFixPatch.Prefix` | `NPauseMenu.CloseToMenu()` | Prefix | 无参数；读静态按钮字段 | 元数据支持；尚未做场景行为验证 |

完整矩阵见 [`phase2-quickrestart-patch-matrix.csv`](phase2-quickrestart-patch-matrix.csv)。
其核心 `RestartRoomAsync` 不是 Harmony hook，而是 Mod 初始化后由新增按钮/按键调用；即使四个
hook 全部织入，如果配置注册、PCK、本地化和入口初始化没有执行，Mod 仍不完整。

## 4. 已验证的真实补丁转换

候选：`BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch.GetAngle`：

- 目标：`float HandPosHelper.GetAngle(int handSize, int cardIndex)`；
- 类型：bool Prefix；
- 参数：`handSize`、`cardIndex`、`ref float __result`；
- 选择理由：纯数学逻辑，不依赖场景树、资源、存档或动态目标；
- 原 hook/类型非公开，`harmony-one` 只在输出的 BaseLib 副本提升可见性；Steam DLL 不变。

**已由自动化测试验证**：

| 输入 | 原版 | Harmony 2.4.2 | Cecil 静态织入 |
|---|---:|---:|---:|
| `(10, 0)` | -15 | -15 | -15 |
| `(12, 0)` | 抛 `ArgumentOutOfRangeException` | -14.656751 | -14.656751 |
| `(12, 5)` | 抛 `ArgumentOutOfRangeException` | -1.3620203 | -1.3620203 |
| `(14, 13)` | 抛 `ArgumentOutOfRangeException` | 13.370044 | 13.370044 |

测试分别在独立进程执行原版、Harmony 运行时版和静态织入版，避免 Harmony 的进程级方法
替换污染对照结果。修改后的游戏和 BaseLib 程序集仅存在于 `.work/phase2`。

## 5. 游戏 Mod 加载与静态集成边界

**仅完成静态分析**（来自实际 `sts2.dll` IL）：

1. `ModManager` 从本地目录和 Steam Workshop install info 递归读取 manifest。
2. `SortModList` 根据 manifest dependencies 做拓扑排序，并以用户配置顺序作为队列优先级；
   循环依赖会被记录。
3. `TryLoadMod` 用当前 `AssemblyLoadContext.LoadFromAssemblyPath` 动态加载 DLL，并用
   `ProjectSettings.LoadResourcePack` 加载 PCK。
4. 它对 `Assembly.GetTypes()` 的结果查找 `[ModInitializer]`，再由
   `CallModInitializer` 反射调用指定的静态入口。
5. 初始化完成后，游戏本身还会对整个 Mod assembly 调用 `Harmony.PatchAll(assembly)`。

因此静态 iOS 路径必须同时解决：

- 不在运行时 `AssemblyLoad` 任意 DLL；Mod assembly 必须在构建期进入 AOT 引用闭包并保根。
- 静态织入后，BaseLib/QuickRestart 初始化器内部的 PatchAll 和游戏加载器末尾的 PatchAll
  都必须按“已静态编译 Mod”跳过，否则会再次尝试动态 patch。
- 初始化器中的非 Harmony 注册逻辑仍需执行，不能简单删除整个入口。
- 未被静态 hook 直接引用但由反射、Godot 脚本扫描、配置或资源实例化的类型，必须用
  `TrimmerRootAssembly`、linker descriptor 或生成的直接引用保留。
- PCK 资源可在构建期合入内容包，或保留 `LoadResourcePack` 的外置资源路径；代码 DLL 则必须 AOT。

## 6. Windows NativeAOT

**尚未验证（环境阻碍）**

独立 `PublishAot` 冒烟项目已经加入并实际执行。还原和 C# 编译成功，但本机缺少 Visual Studio
2022 “Desktop development with C++” linker，ILCompiler 报：

```text
Platform linker not found ... Desktop Development for C++ workload in Visual Studio
```

这不是 iOS 结论。安装 Windows C++ Build Tools 后可重跑
`pwsh tests/STS2Weaver.Phase2/run-aot-smoke.ps1`；即使通过，也只证明 Windows NativeAOT 环境。

## 7. 可重复命令

```powershell
# Phase 1 回归
pwsh tests/STS2Weaver.Phase1/run.ps1

# 自动定位 Steam/Workshop，执行真实 BaseLib 三路行为对照
pwsh tests/STS2Weaver.Phase2/run-real-mod.ps1

# 任意实际 Mod 的完整 Harmony/AOT 元数据审计
dotnet run --project src/STS2Weaver -c Release -- analyze-harmony `
  <mod.dll> <sts2.dll> <report.json>

# Windows NativeAOT 环境测试
pwsh tests/STS2Weaver.Phase2/run-aot-smoke.ps1
```
