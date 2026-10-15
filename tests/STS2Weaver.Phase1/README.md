# Phase 1：Harmony 静态织入验证

这组 fixture 完全独立于游戏本体，用真实 Harmony 2 特性声明 Prefix/Postfix，再由
`STS2Weaver harmony` 读取特性元数据并静态写入目标程序集。测试不会调用 Harmony 的运行时
patch API，因此验证的是适用于 NativeAOT 的构建期路径。

```powershell
pwsh tests/STS2Weaver.Phase1/run.ps1
```

覆盖行为：

- `bool Prefix` 返回 `false`，跳过原方法并通过 `ref __result` 提供返回值，随后仍执行 Postfix；
- Prefix 通过 `ref` 修改原方法参数；
- Postfix 通过 `ref __result` 修改返回值；
- 多分支方法的所有 `ret` 汇入同一 Postfix 尾声；
- `__instance`、原方法同名参数与补丁副作用正确绑定。
- `Type[]` 目标签名可在重载方法中正确消歧；
- 旧 manifest CLI 行为保持不变；
- Transpiler 会以明确诊断失败，且不会生成部分织入产物。

当前原型会明确拒绝 Transpiler、Finalizer、动态 `TargetMethod(s)`、泛型目标，以及无法静态
解析的 HarmonyPatch 声明，不会静默忽略。
