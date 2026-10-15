using System.Reflection;
using HarmonyLib;
using STS2Weaver.Phase1.FixtureTarget;

namespace STS2Weaver.Phase3.MatrixDynamicTargetMod;

[HarmonyPatch]
public static class DynamicPatch
{
    [HarmonyTargetMethod]
    public static MethodBase TargetMethod() => typeof(Calculator).GetMethod(nameof(Calculator.GetValue))!;

    [HarmonyPrefix]
    public static void Prefix() { }
}
