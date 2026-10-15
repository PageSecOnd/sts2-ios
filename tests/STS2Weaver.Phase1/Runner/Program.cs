using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1)
    throw new ArgumentException("usage: Runner <woven FixtureTarget.dll>");

string wovenPath = Path.GetFullPath(args[0]);
string dependencyDirectory = Path.GetDirectoryName(wovenPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string candidate = Path.Combine(dependencyDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var targetAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(wovenPath);
var calculatorType = targetAssembly.GetType("STS2Weaver.Phase1.FixtureTarget.Calculator", throwOnError: true)!;
var calculator = Activator.CreateInstance(calculatorType)!;
calculatorType.GetMethod("Reset")!.Invoke(null, null);

var modAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dependencyDirectory, "FixtureMod.dll"));
var patchState = modAssembly.GetType("STS2Weaver.Phase1.FixtureMod.PatchState", throwOnError: true)!;
patchState.GetMethod("Reset")!.Invoke(null, null);

AssertEqual(42, InvokeInt("GetValue"), "bool Prefix 应跳过原方法，Postfix 仍应修改 __result");
AssertEqual(0, GetStaticInt(calculatorType, "OriginalGetValueCalls"), "被跳过的原方法不应产生副作用");

AssertEqual(30, InvokeInt("Add", 2, 3), "Prefix 应修改 ref 参数，Postfix 应修改 ref __result");
AssertEqual(1, GetStaticInt(calculatorType, "AddCalls"), "Add 原方法应执行一次");
AssertEqual(12, GetStaticInt(calculatorType, "LastAddLeft"), "原方法应看到 Prefix 修改后的参数");

AssertEqual(99, InvokeInt("Classify", -3), "Postfix 应覆盖负数分支返回点");
AssertEqual(100, InvokeInt("Classify", 0), "Postfix 应覆盖零分支返回点");
AssertEqual(102, InvokeInt("Classify", 4), "Postfix 应覆盖正偶数分支返回点");
AssertEqual(101, InvokeInt("Classify", 3), "Postfix 应覆盖正奇数分支返回点");

AssertEqual(2, GetStaticInt(patchState, "PrefixCalls"), "两个 Prefix 都应各执行一次");
AssertEqual(6, GetStaticInt(patchState, "PostfixCalls"), "跳过原方法后及其他路径的 Postfix 都应执行");
AssertEqual(true, (bool)patchState.GetProperty("SawInstance")!.GetValue(null)!, "__instance 应正确绑定");

Console.WriteLine("[PASS] Phase 1 Harmony Prefix/Postfix 静态织入行为验证通过");
return;

int InvokeInt(string method, params object[] parameters) =>
    (int)calculatorType.GetMethod(method, parameters.Select(x => x.GetType()).ToArray())!
        .Invoke(calculator, parameters)!;

static int GetStaticInt(Type type, string property) => (int)type.GetProperty(property)!.GetValue(null)!;

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}
