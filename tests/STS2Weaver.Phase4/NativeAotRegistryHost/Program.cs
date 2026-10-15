using System.Runtime.CompilerServices;
using STS2.StaticMods;
using STS2Weaver.Phase1.FixtureTarget;
using STS2Weaver.Phase3.MatrixDependency;
using STS2Weaver.Phase3.MatrixDependentMod;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("expected NativeAOT without dynamic code");

// 首次触及织入目标时，它的 <Module>..cctor 必须调用生成注册器。
int result = new Calculator().Add(1, 2);
if (!GeneratedStaticModRegistry.IsInitialized || GeneratedStaticModRegistry.InitializationCount != 2)
    throw new InvalidOperationException("generated registry did not initialize exactly two mods");
if (DependencyModEntry.Calls != 1 || ModEntry.Calls != 1)
    throw new InvalidOperationException("mod initializer was not called exactly once");
if (!InitializationLog.Values.SequenceEqual(["dependency", "dependent"]))
    throw new InvalidOperationException($"wrong initialization order: {string.Join(',', InitializationLog.Values)}");

// 再调两次必须幂等，不能重复初始化。
GeneratedStaticModRegistry.InitializeAll();
GeneratedStaticModRegistry.InitializeAll();
if (DependencyModEntry.Calls != 1 || ModEntry.Calls != 1)
    throw new InvalidOperationException("duplicate initialization was not suppressed");

if (result != 3) throw new InvalidOperationException($"unexpected target behavior: {result}");
Type rooted = Type.GetType(
    "STS2Weaver.Phase3.MatrixDependentMod.TrimOnlyType, MatrixDependentMod", throwOnError: true)!;
object instance = Activator.CreateInstance(rooted)
    ?? throw new InvalidOperationException("trim-rooted constructor unavailable");
int value = (int)(rooted.GetProperty("Value")?.GetValue(instance)
    ?? throw new InvalidOperationException("trim-rooted property unavailable"));
if (value != 42) throw new InvalidOperationException("trim-rooted reflected type returned wrong value");

Console.WriteLine("[PASS] NativeAOT generated registry: dependency -> dependent; once-only; reflection roots preserved");
