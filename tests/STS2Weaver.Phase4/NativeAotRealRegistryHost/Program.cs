using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Helpers;
using STS2.StaticMods;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("expected NativeAOT without dynamic code");

float actual = HandPosHelper.GetAngle(12, 5);
if (MathF.Abs(actual - -1.3620203f) > 0.0001f)
    throw new InvalidOperationException($"real BaseLib static Prefix returned {actual}");
if (!GeneratedStaticModRegistry.IsInitialized || GeneratedStaticModRegistry.InitializationCount != 1)
    throw new InvalidOperationException("real BaseLib selected patch was not registered exactly once");

GeneratedStaticModRegistry.InitializeAll();
if (GeneratedStaticModRegistry.InitializationCount != 1)
    throw new InvalidOperationException("real BaseLib registration repeated");

Console.WriteLine($"[PASS] real BaseLib registered once and executed under NativeAOT: {actual:R}");
