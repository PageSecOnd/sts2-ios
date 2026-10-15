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
    public static bool Initialized { get; private set; }
    public static void Initialize() => Initialized = true;
}
