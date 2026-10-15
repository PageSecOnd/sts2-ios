using System.Runtime.CompilerServices;
using STS2Weaver.Phase1.FixtureTarget;
using STS2Weaver.Phase3.MatrixDependentMod;
using STS2Weaver.Phase3.MatrixDependency;

if (RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("expected NativeAOT without dynamic code");
Action[] compileTimeInitializers = [ModEntry.Initialize];
foreach (Action initialize in compileTimeInitializers) initialize();
if (!ModEntry.Initialized) throw new InvalidOperationException("compile-time Mod initializer did not execute");
_ = typeof(DependentPatch); // 显式类型可达性边，不做程序集扫描。
int value = new Calculator().Add(1, 2);
if (value != 8) throw new InvalidOperationException($"dependent postfix expected 8, got {value}");
if (DependencyApi.InitializationCount != 1) throw new InvalidOperationException("dependency static constructor did not run exactly once");
if (DependencyApi.Identity(42) != 42) throw new InvalidOperationException("explicit generic instantiation failed");
Console.WriteLine("[PASS] NativeAOT static mod dependency executed: 1+2+5=8");
