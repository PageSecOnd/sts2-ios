# Phase 4：iOS NativeAOT 构建链与静态 Mod 注册

## 实际构建契约

当前仓库没有 `ios-export/Directory.Build.props`。Godot/ILC 设置、主程序集替换和 trimming root
都在 `ios-export/sts2.csproj`。现有流程不能通过向 thin shim 添加普通源码来注册 Mod：
`CoreCompile` 后，shim 产出的主程序集会被织入后的真实 `sts2.dll` 覆盖，新增源码也随之消失。

Phase 4 因此生成独立的 `STS2StaticModRegistry.dll`，再用 Cecil 在游戏副本的
`<Module>..cctor` 开头插入静态 `InitializeAll` 调用。注册器按依赖拓扑直接引用入口方法，使用
`Interlocked.CompareExchange` 保证一次性执行，没有 `Assembly.Load`、反射扫描、Emit 或
`PatchAll`。注册器被游戏程序集直接引用，因此会进入 ILC 可达闭包。

执行阶段严格区分：

1. **构建期 hook 转换**：Harmony 元数据转成 Cecil 静态调用；
2. **模块载入/登记**：生成注册器按拓扑执行 AOT 安全入口；
3. **Godot 入口**：原 `godotsharp_game_main_init` 继续负责主 assembly 的 Godot 脚本登记；
4. **游戏生命周期初始化**：需要场景树、配置系统或资源包的逻辑必须由后续显式 hook 触发；
5. **资源加载**：PCK/GDExtension 独立处理，注册成功不代表资源成功。

当前真实 BaseLib manifest 只有 `HandPosHelperGetPositionPatch` root，没有调用
`BaseLibMain.Initialize`。因此验证的是选择性真实 hook 和静态登记，不是完整 BaseLib。

## Manifest 与裁剪

`static-registry` 校验：schema、目标 assembly 名称/版本/MVID/SHA256、Mod assembly 版本、重复
ID、缺失依赖和循环依赖。manifest 声明顺序可以任意，输出固定为依赖优先。

初始化入口必须是 `public static void Method()`。无需运行入口但要参与 AOT 的类型放在
`rootTypes`。生成器同时产生 linker descriptor，`preserve="all"` 保留这些类型的构造函数、
方法和反射元数据；其它 Mod 类型仍按静态可达性裁剪，避免粗暴 root 整个 BaseLib。

命令：

```text
STS2Weaver static-registry manifest.json target.dll assembly-dir registry.dll roots.xml
STS2Weaver inject-registry target.dll registry.dll output.dll dependency-dir
STS2Weaver verify-static-input manifest.json mod-dir game-deps-dir report.json
```

## iOS 输入链

```text
正版 macOS 游戏程序集副本
  -> STS2MobileIos 原有织入
  -> 选中 Mod hook 静态织入
  -> manifest 目标/版本/依赖验证
  -> 生成注册器和 trimming descriptor
  -> 注入一次性模块注册调用
  -> prebuilt/sts2.dll + prebuilt/deps/*.dll
  -> Godot.NET.Sdk 4.5.1 / IlcCompile
  -> ios-arm64 sts2.dylib
  -> Godot Xcode 工程/xcframework
```

不设置 `STS2_STATIC_MOD_DLL` 和 `STS2_STATIC_MOD_MANIFEST` 时，脚本明确走无 Mod 路径。设置代码
Mod 却不提供 manifest 会失败，防止出现“hook 织入了但 Mod 未登记”的半成品。

Windows 真实输入脚本会输出同形目录并再次运行 NativeAOT，但本机只有
`data_sts2_windows_x86_64`，没有 `data_sts2_macos_arm64`，所以两个平台的 MVID/SHA256 尚不能
比较。macOS 构建必须针对 Mac 版 `sts2.dll` 重新织入并重新解析 manifest；不能把 Windows
原生库带进 iOS。托管程序集即使同名也必须由 manifest 身份检查确认。

## 公开 macOS ARM64 验证

公开 workflow 固定使用 `macos-15`、.NET 9.0.319、Godot 4.5.1 mono 和同版 export templates。
它构建公开 Godot C# fixture，生成并注入两个有依赖关系的静态 Mod，然后执行
`dotnet publish -r ios-arm64 -p:GodotTargetPlatform=ios`。验证项目包括：

- Runner 确实为 `arm64`；
- Xcode 和 iPhoneOS SDK 可用；
- 产物是 arm64 Mach-O dylib；
- 导出 `godotsharp_game_main_init`；
- 没有 Mono/CoreCLR 动态依赖；
- Artifact 只保存文本日志，不保存 dylib。

该 workflow 已在用户 Fork 的 GitHub Actions 上实际运行通过。下载的诊断 Artifact 已在本地
核验（ZIP SHA-256：`DB5B8C50E101D9F0899D1B548D9BBFD700C1CFA692E5ACC73D745C1918180321`），
结果如下：

- Runner：macOS 15.8 / ARM64；Xcode 16.4；iPhoneOS SDK 18.5；
- .NET SDK：9.0.319（`global.json` 固定选择），Godot：
  `4.5.1.stable.mono.official.f62fdbde1`；
- 静态注册顺序为 `foundation -> dependent`，注册器成功生成，`InitializeAll` 成功注入公开
  fixture 的模块初始化器；
- `dotnet publish -r ios-arm64` 成功生成 19,778,032 字节的
  `Phase4IosAotFixture.dylib`；
- `file`/`lipo` 确认产物为 `Mach-O 64-bit dynamically linked shared library arm64`；
- `nm` 确认导出 `_godotsharp_game_main_init`；
- `otool -L` 只列出 iOS 系统、Swift、Foundation、Security 等依赖，没有 Mono 或 CoreCLR。

这证明公开 fixture 的静态 Mod 注册代码确实进入了 ios-arm64 NativeAOT 产物，但没有证明该
dylib 已被 Godot Xcode 工程打包或在真机执行。Artifact 按设计只含文本诊断，不含 dylib。

成功构建仍有两组待处理警告：

1. `Microsoft.DotNet.ILCompiler` 9.0.21 触发 `NU1903`，对应高严重性公告
   `GHSA-p3jp-9v83-76w9`。在确认 Godot 4.5.1 与更新 .NET 9 runtime patch 的兼容性前，不能
   把该结果描述为无已知风险；
2. Godot 4.5.1 的 `DelegateUtils`、`ScriptManagerBridge` 等代码触发 IL2026、IL207x、IL3050、
   IL2055 等 trimming/AOT 警告。公开 fixture 成功链接不代表真实游戏用到的所有反射、泛型
   实例化路径都可运行，必须继续通过真实输入和真机覆盖。

此外，链接器报告 iOS 最低版本参数 12.0 被 target 12.2 覆盖，且 `-ld_classic` 已弃用；当前
均未阻塞 dylib 生成。GitHub 标准 `macos-15` 当前是 3 核 M1、7 GB RAM、14 GB SSD；完整游戏
ILC 很可能超过其资源，届时应改用私有/自托管 Mac 或 `macos-15-xlarge`，而不是删减诊断后
宣称成功。规格来源：
[GitHub-hosted runners reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)。

## FMOD、Spine 与真实游戏私有构建

公开 fixture 不包含 FMOD、Spine 或游戏 PCK，因此它只能验证 Godot/.NET/iOS AOT 契约。
完整游戏仍需要用户自行合法取得：FMOD 2.03 iOS 静态库、对应 Godot 4.5.1 的 FMOD
GDExtension、Spine 4.2/Godot 4.5.1 framework，以及正版 PCK。缺少任何一项都不能称为完整
游戏构建成功，也不得用空库替代。

真实私有构建只能在用户额外授权后通过私有临时传输或自托管 runner。禁止把这些输入放进
Git、Actions cache、公共 Artifact 或日志。建议最小权限流程：临时工作目录、构建后删除、
Artifact 仅输出用户自己的最终加密/签名产物，并关闭包含路径/文件列表的冗余日志。

## 签名、entitlement 与阶段判定

构建成功必须逐级记录，不能合并：

1. ios-arm64 NativeAOT dylib；
2. Godot Xcode 工程；
3. IPA 打包；
4. IPA 签名；
5. iPad 安装；
6. 启动进入游戏；
7. 静态 Mod 行为发生。

`com.apple.developer.kernel.increased-memory-limit` 只是请求支持设备提供更高内存，不保证一定
可用。iOS/iPadOS 上签名 entitlement 必须得到 embedded provisioning profile 授权；把键手写
进 plist 或由第三方重签工具保留下来并不等于系统接受。当前没有用户证书/profile，因此无法
确认其实际 Apple 身份是否获准。Apple 的
[entitlement 文档](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.kernel.increased-memory-limit)
明确说明额外内存只在部分设备上可用；profile 授权关系见
[TN3125](https://developer.apple.com/documentation/technotes/tn3125-inside-code-signing-provisioning-profiles)。
拿到 profile 后必须同时检查：

```bash
security cms -D -i MyProfile.mobileprovision > profile.plist
/usr/libexec/PlistBuddy -c 'Print :Entitlements:com.apple.developer.kernel.increased-memory-limit' profile.plist
codesign -d --entitlements :- Payload/StS2.app
```

两边都为 `true` 且 application-identifier/bundle ID/team 匹配，才进入真机安装。普通 Windows
重签/侧载若换了 bundle ID 或使用不含该 entitlement 的 profile，必须按“不具备大内存权限”
处理。升级始终使用同 bundle ID 覆盖安装，绝不 uninstall，以保留 Documents 和存档。
