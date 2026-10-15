# Phase 4：iOS NativeAOT 输入与静态 Mod 注册

Windows 可执行测试：

```powershell
pwsh tests/STS2Weaver.Phase4/run-static-registry.ps1
pwsh tests/STS2Weaver.Phase4/run-real-baselib-registry-aot.ps1
```

第一项使用公开夹具验证空输入、单 Mod、依赖逆序、拓扑排序、重复初始化、依赖缺失、循环、
游戏版本不匹配和 trimming roots，并运行 Windows NativeAOT。第二项只读定位用户自己的
Steam/Workshop 文件，构造与 iOS `prebuilt` 同形的托管输入，再运行真实 BaseLib Prefix。

公开 macOS/iOS 验证入口是 `.github/workflows/phase4-ios-nativeaot-public.yml`。它只使用仓库内
夹具和 Godot 官方公开下载，不读取 secrets，不缓存或上传游戏、Mod、FMOD、Spine、PCK、证书
或 provisioning profile；Artifact 仅含文本诊断日志。
