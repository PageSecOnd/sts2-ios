using HarmonyLib;
using STS2Weaver.Phase1.FixtureTarget;

namespace STS2Weaver.Phase1.FixtureMod;

public static class PatchState
{
    public static int PrefixCalls { get; set; }
    public static int PostfixCalls { get; set; }
    public static bool SawInstance { get; set; }

    public static void Reset()
    {
        PrefixCalls = 0;
        PostfixCalls = 0;
        SawInstance = false;
    }
}

[HarmonyPatch(typeof(Calculator), nameof(Calculator.GetValue))]
public static class GetValuePatch
{
    [HarmonyPrefix]
    public static bool Prefix(Calculator __instance, ref int __result)
    {
        PatchState.PrefixCalls++;
        PatchState.SawInstance |= __instance != null;
        __result = 41;
        return false;
    }

    [HarmonyPostfix]
    public static void Postfix(ref int __result)
    {
        PatchState.PostfixCalls++;
        __result++;
    }
}

[HarmonyPatch(typeof(Calculator), nameof(Calculator.Add), typeof(int), typeof(int))]
public static class AddPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref int left)
    {
        PatchState.PrefixCalls++;
        left += 10;
    }

    [HarmonyPostfix]
    public static void Postfix(ref int __result)
    {
        PatchState.PostfixCalls++;
        __result *= 2;
    }
}

[HarmonyPatch(typeof(Calculator), nameof(Calculator.Classify))]
public static class ClassifyPatch
{
    [HarmonyPostfix]
    public static void Postfix(Calculator __instance, int value, ref int __result)
    {
        PatchState.PostfixCalls++;
        PatchState.SawInstance |= __instance != null;
        __result += 100;
    }
}
