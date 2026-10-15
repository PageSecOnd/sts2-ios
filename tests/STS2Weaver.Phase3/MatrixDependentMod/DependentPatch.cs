using HarmonyLib;
using STS2Weaver.Phase1.FixtureTarget;
using STS2Weaver.Phase3.MatrixDependency;

namespace STS2Weaver.Phase3.MatrixDependentMod;

[HarmonyPatch(typeof(Calculator), nameof(Calculator.Add), typeof(int), typeof(int))]
public static class DependentPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref int __result) => __result += DependencyApi.Bonus;
}

public static class ModEntry
{
    public static int Calls { get; private set; }
    public static bool Initialized => Calls > 0;
    public static void Initialize()
    {
        Calls++;
        InitializationLog.Add("dependent");
    }
}

// 仅由 Phase 4 manifest/linker descriptor 保留，用来验证字符串反射下的类型和构造函数。
public sealed class TrimOnlyType
{
    public int Value { get; } = 42;
}
