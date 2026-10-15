using System.Runtime.CompilerServices;
using BaseLib.Patches.Hooks;
using MegaCrit.Sts2.Core.Helpers;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("本测试必须运行在无 JIT 的 NativeAOT 进程中");

StaticModRegistry.InitializeAll();
if (!BaseLibStaticAdapter.WasInitialized)
    throw new InvalidOperationException("BaseLib 编译期注册入口未执行");

var cases = new (int HandSize, int CardIndex, float Expected)[]
{
    (10, 0, -15f),
    (12, 0, -14.656751f),
    (12, 5, -1.3620203f),
    (14, 13, 13.370044f)
};

foreach (var test in cases)
{
    float actual = HandPosHelper.GetAngle(test.HandSize, test.CardIndex);
    if (MathF.Abs(actual - test.Expected) > 0.0001f)
        throw new InvalidOperationException(
            $"BaseLib 静态 Prefix 结果不一致: ({test.HandSize}, {test.CardIndex}) expected={test.Expected}, actual={actual}");
    Console.WriteLine($"[RESULT] {test.HandSize},{test.CardIndex}={actual:R}");
}

Console.WriteLine("[PASS] NativeAOT executed the statically woven real BaseLib GetAngle Prefix without Harmony runtime patching");

internal static class StaticModRegistry
{
    // 编译期固定注册表：没有 Assembly.Load、反射扫描或运行时生成代码。
    private static readonly Action[] Initializers = [BaseLibStaticAdapter.Initialize];

    public static void InitializeAll()
    {
        foreach (Action initializer in Initializers)
            initializer();
    }
}

internal static class BaseLibStaticAdapter
{
    public static bool WasInitialized { get; private set; }

    public static void Initialize()
    {
        // typeof 对提升后的真实 BaseLib hook 建立显式可达边；实际 hook 由织入目标直接调用。
        RuntimeHelpers.RunClassConstructor(typeof(HandPosHelperGetPositionPatch).TypeHandle);
        WasInitialized = true;
        Console.WriteLine("[INIT] BaseLib selected-patch static adapter");
    }
}
