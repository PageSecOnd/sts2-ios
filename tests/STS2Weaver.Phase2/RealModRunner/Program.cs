using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using Mono.Cecil;

if (args.Length != 4 || args[0] is not ("original" or "harmony" or "woven"))
    throw new ArgumentException("usage: RealModRunner <original|harmony|woven> <sts2.dll> <BaseLib.dll> <game-data-dir>");

string mode = args[0];
string gameAssemblyPath = Path.GetFullPath(args[1]);
string baseLibPath = Path.GetFullPath(args[2]);
string gameDataDirectory = Path.GetFullPath(args[3]);

if (mode == "woven") VerifyStaticCall(gameAssemblyPath, baseLibPath);

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    if (name.Name == "BaseLib")
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(baseLibPath);

    string dependency = Path.Combine(gameDataDirectory, name.Name + ".dll");
    return File.Exists(dependency)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency)
        : null;
};

var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameAssemblyPath);
if (mode == "harmony")
{
    var baseLibAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(baseLibPath);
    var patchType = baseLibAssembly.GetType(
        "BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch", throwOnError: true)!;
    new Harmony("sts2-ios.phase2.baseline").CreateClassProcessor(patchType).Patch();
}

var helperType = gameAssembly.GetType("MegaCrit.Sts2.Core.Helpers.HandPosHelper", throwOnError: true)!;
var getAngle = helperType.GetMethod("GetAngle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(helperType.FullName, "GetAngle");

var cases = new (int HandSize, int CardIndex)[] { (10, 0), (12, 0), (12, 5), (14, 13) };
var results = cases.Select(Probe).ToArray();

Console.WriteLine(JsonSerializer.Serialize(new { Mode = mode, Results = results }));
return;

ProbeResult Probe((int HandSize, int CardIndex) test)
{
    try
    {
        return new ProbeResult(test.HandSize, test.CardIndex,
            Convert.ToSingle(getAngle.Invoke(null, [test.HandSize, test.CardIndex])), null);
    }
    catch (TargetInvocationException e)
    {
        return new ProbeResult(test.HandSize, test.CardIndex, null,
            $"{e.InnerException?.GetType().FullName}: {e.InnerException?.Message}");
    }
}

static void VerifyStaticCall(string gamePath, string modPath)
{
    using var game = AssemblyDefinition.ReadAssembly(gamePath);
    var target = game.MainModule.GetType("MegaCrit.Sts2.Core.Helpers.HandPosHelper")
        ?.Methods.Single(x => x.Name == "GetAngle");
    bool hasCall = target?.Body.Instructions.Any(instruction =>
        instruction.Operand is MethodReference called
        && called.DeclaringType.FullName == "BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch"
        && called.Name == "GetAngle") == true;
    if (!hasCall) throw new InvalidOperationException("织入目标 IL 中没有找到 BaseLib GetAngle 静态调用");
    if (!game.MainModule.AssemblyReferences.Any(x => x.Name == "BaseLib"))
        throw new InvalidOperationException("织入目标缺少 BaseLib assembly reference");

    using var mod = AssemblyDefinition.ReadAssembly(modPath);
    var hookType = mod.MainModule.GetType("BaseLib.Patches.Hooks.HandPosHelperGetPositionPatch")
        ?? throw new InvalidOperationException("Mod 副本缺少 hook 类型");
    var hook = hookType.Methods.Single(x => x.Name == "GetAngle");
    if (!hookType.IsPublic || !hook.IsPublic || !hook.IsStatic)
        throw new InvalidOperationException("独立 Mod 副本中的 hook 尚不可跨程序集静态调用");
}

sealed record ProbeResult(int HandSize, int CardIndex, float? Result, string? Error);
