using System.Runtime.CompilerServices;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("NativeAOT 进程不应支持或编译动态代码");

Console.WriteLine("[PASS] Windows NativeAOT smoke test");
