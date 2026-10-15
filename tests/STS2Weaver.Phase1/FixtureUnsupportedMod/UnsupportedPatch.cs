using System.Reflection.Emit;
using HarmonyLib;
using STS2Weaver.Phase1.FixtureTarget;

namespace STS2Weaver.Phase1.FixtureUnsupportedMod;

[HarmonyPatch(typeof(Calculator), nameof(Calculator.GetValue))]
public static class UnsupportedPatch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => instructions;
}
