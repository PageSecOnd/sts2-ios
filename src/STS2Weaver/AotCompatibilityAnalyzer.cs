using System.Text.Json;
using Mono.Cecil;

static class AotCompatibilityAnalyzer
{
    public static int Run(string assemblyPath, string dependencyDirectory, string reportPath)
    {
        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var diagnostics = new List<AotDiagnostic>();
            CheckDependencies(assembly, dependencyDirectory, diagnostics);
            CheckMethodBodies(assembly, diagnostics);

            var report = new
            {
                SchemaVersion = 1,
                Assembly = assembly.Name.FullName,
                DependenciesDirectory = Path.GetFullPath(dependencyDirectory),
                Compatible = diagnostics.All(x => x.Severity != "error"),
                Diagnostics = diagnostics,
                StaticConstructors = assembly.MainModule.GetTypes()
                    .SelectMany(x => x.Methods)
                    .Where(x => x.IsConstructor && x.IsStatic)
                    .Select(x => x.FullName)
                    .Order()
                    .ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));

            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine($"[{diagnostic.Severity.ToUpperInvariant()}] {diagnostic.Code}: {diagnostic.Location}: {diagnostic.Message}");
            if (diagnostics.Any(x => x.Severity == "error"))
            {
                Console.Error.WriteLine($"[FAIL] NativeAOT 预检不兼容，报告: {reportPath}");
                return 1;
            }
            Console.WriteLine($"[OK] NativeAOT 预检通过，报告: {reportPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[FAIL] NativeAOT 预检失败: {e.Message}");
            return 1;
        }
    }

    private static void CheckDependencies(AssemblyDefinition assembly, string dependencyDirectory,
        List<AotDiagnostic> diagnostics)
    {
        foreach (var reference in assembly.MainModule.AssemblyReferences)
        {
            // Harmony 特性只用于构建期发现；若方法体仍调用 Patch API，AOT2003 会单独报错。
            if (IsFrameworkAssembly(reference.Name) || reference.Name == "0Harmony") continue;
            string candidate = Path.Combine(dependencyDirectory, reference.Name + ".dll");
            if (!File.Exists(candidate))
                diagnostics.Add(new("AOT1001", "error", assembly.MainModule.Name,
                    $"缺少程序集依赖 {reference.FullName}；请把依赖放入 NativeAOT 输入目录"));
        }
    }

    private static void CheckMethodBodies(AssemblyDefinition assembly, List<AotDiagnostic> diagnostics)
    {
        foreach (var method in assembly.MainModule.GetTypes().SelectMany(x => x.Methods).Where(x => x.HasBody))
        {
            foreach (var called in method.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>())
            {
                string type = called.DeclaringType.FullName;
                if (called.DeclaringType.Namespace == "System.Reflection.Emit"
                    || type is "System.Reflection.Emit.DynamicMethod" or "System.Reflection.Emit.AssemblyBuilder")
                    diagnostics.Add(new("AOT2001", "error", method.FullName,
                        $"Reflection.Emit/动态 IL 不可用于 NativeAOT: {called.FullName}"));
                else if (type == "System.Reflection.Assembly" && called.Name.StartsWith("Load", StringComparison.Ordinal))
                    diagnostics.Add(new("AOT2002", "error", method.FullName,
                        $"运行时程序集加载不属于静态 Mod 闭包: {called.FullName}"));
                else if (type == "System.Runtime.Loader.AssemblyLoadContext" && called.Name.StartsWith("Load", StringComparison.Ordinal))
                    diagnostics.Add(new("AOT2002", "error", method.FullName,
                        $"运行时程序集加载不属于静态 Mod 闭包: {called.FullName}"));
                else if (called.DeclaringType.Namespace == "HarmonyLib"
                         && called.Name.Contains("Patch", StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(new("AOT2003", "error", method.FullName,
                        $"仍调用 Harmony 运行时补丁 API；静态织入后必须旁路此路径: {called.FullName}"));
                else if (called.Name is "MakeGenericType" or "MakeGenericMethod")
                    diagnostics.Add(new("AOT3001", "warning", method.FullName,
                        $"运行时泛型实例化需要显式保留所有闭包: {called.FullName}"));
                else if (type == "System.Activator" && called.Name == "CreateInstance")
                    diagnostics.Add(new("AOT3002", "warning", method.FullName,
                        $"反射构造需要 trimming 描述或 DynamicDependency: {called.FullName}"));
            }
        }
    }

    private static bool IsFrameworkAssembly(string name) =>
        name is "mscorlib" or "netstandard" or "System" or "System.Core"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private sealed record AotDiagnostic(string Code, string Severity, string Location, string Message);
}
