using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace STS2Weaver.Phase3.MatrixRiskMod;

public static class RiskyCode
{
    public static DynamicMethod Generate() => new("Generated", typeof(void), Type.EmptyTypes);
    public static Assembly Load(byte[] bytes) => Assembly.Load(bytes);
    public static void PatchAtRuntime() => new Harmony("phase3.risk").PatchAll();
}
