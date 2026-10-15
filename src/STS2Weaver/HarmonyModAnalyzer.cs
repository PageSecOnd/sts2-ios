using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class HarmonyModAnalyzer
{
    private const string PatchAttribute = "HarmonyLib.HarmonyPatch";
    private static readonly Dictionary<string, string> PatchKinds = new()
    {
        ["HarmonyLib.HarmonyPrefix"] = "Prefix",
        ["HarmonyLib.HarmonyPostfix"] = "Postfix",
        ["HarmonyLib.HarmonyTranspiler"] = "Transpiler",
        ["HarmonyLib.HarmonyFinalizer"] = "Finalizer"
    };

    public static int Run(string modPath, string targetPath, string outputPath)
    {
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(modPath)));
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath)));
            var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
            using var mod = AssemblyDefinition.ReadAssembly(modPath, parameters);
            using var target = AssemblyDefinition.ReadAssembly(targetPath, parameters);

            var patches = AnalyzePatches(mod, target);
            if (Path.GetExtension(outputPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                WriteCsv(outputPath, patches);
                Console.WriteLine($"[OK] Harmony 审计: {patches.Count} 个补丁方法 → {outputPath}");
                return 0;
            }
            var report = new
            {
                SchemaVersion = 1,
                Assembly = mod.Name.FullName,
                ModuleVersionId = mod.MainModule.Mvid,
                References = mod.MainModule.AssemblyReferences.Select(x => x.FullName).Order().ToArray(),
                Initializers = FindInitializers(mod),
                HarmonyPatches = patches,
                Summary = new
                {
                    Total = patches.Count,
                    Prefix = patches.Count(x => x.Kind == "Prefix"),
                    Postfix = patches.Count(x => x.Kind == "Postfix"),
                    Transpiler = patches.Count(x => x.Kind == "Transpiler"),
                    Finalizer = patches.Count(x => x.Kind == "Finalizer"),
                    DynamicTargets = patches.Count(x => x.DynamicTarget),
                    Phase1DirectlySupported = patches.Count(x => x.Compatibility == "supported"),
                    SelectivePromotionSupported = patches.Count(x => x.Compatibility == "requires-hook-promotion"),
                    Unsupported = patches.Count(x => x.Compatibility == "unsupported")
                },
                AotRisks = AnalyzeAotRisks(mod)
            };

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[OK] Harmony 审计: {patches.Count} 个补丁方法 → {outputPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[FAIL] Harmony 审计失败: {e.Message}");
            return 1;
        }
    }

    private static void WriteCsv(string outputPath, List<PatchAudit> patches)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var lines = new List<string>
        {
            "PatchType,HookMethod,Kind,TargetType,TargetMethod,TargetParameters,MethodType,DynamicTarget,ResolvedTarget,HookParameters,SpecialParameters,Compatibility,Reasons"
        };
        lines.AddRange(patches.Select(x => string.Join(",", new[]
        {
            x.PatchType, x.HookMethod, x.Kind, x.TargetType, x.TargetMethod,
            x.TargetParameters == null ? null : string.Join(";", x.TargetParameters),
            x.MethodType, x.DynamicTarget.ToString(), x.ResolvedTarget,
            string.Join(";", x.HookParameters), string.Join(";", x.SpecialParameters),
            x.Compatibility, string.Join(";", x.Reasons)
        }.Select(Csv))));
        File.WriteAllLines(outputPath, lines);
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private static List<PatchAudit> AnalyzePatches(AssemblyDefinition mod, AssemblyDefinition target)
    {
        var result = new List<PatchAudit>();
        foreach (var type in mod.MainModule.GetTypes())
        {
            bool dynamicContainer = type.Methods.Any(x => x.Name is "TargetMethod" or "TargetMethods"
                || HasAttribute(x, "HarmonyLib.HarmonyTargetMethod")
                || HasAttribute(x, "HarmonyLib.HarmonyTargetMethods"));

            foreach (var hook in type.Methods)
            {
                foreach (var kind in GetPatchKinds(type, hook))
                {
                    var targetInfo = ReadTarget(type, hook);
                    bool dynamicTarget = dynamicContainer || targetInfo.Type == null || targetInfo.Method == null;
                    var reasons = new List<string>();
                    if (kind is "Transpiler" or "Finalizer") reasons.Add($"{kind} 尚无静态转换器");
                    if (dynamicTarget) reasons.Add("目标依赖 TargetMethod(s) 或无法从特性静态解析");
                    if (!hook.IsStatic) reasons.Add("hook 不是 static");
                    if (targetInfo.MethodType != "Normal") reasons.Add($"Harmony MethodType.{targetInfo.MethodType} 尚未转换为 CLR 方法名");

                    var specialParameters = hook.Parameters.Select(x => x.Name)
                        .Where(x => x.StartsWith("__", StringComparison.Ordinal)).ToArray();
                    foreach (var special in specialParameters)
                    {
                        if (special is "__instance" or "__result") continue;
                        reasons.Add($"特殊参数 {special} 尚不支持");
                    }

                    string? resolvedTarget = null;
                    if (!dynamicTarget && targetInfo.Type != null && targetInfo.Method != null && targetInfo.MethodType == "Normal")
                    {
                        var targetType = target.MainModule.GetType(targetInfo.Type);
                        if (targetType == null) reasons.Add("游戏目标类型不存在");
                        else
                        {
                            var candidates = targetType.Methods.Where(x => x.Name == targetInfo.Method).ToList();
                            if (targetInfo.Parameters is { Count: > 0 })
                                candidates = candidates.Where(x => SignatureMatches(x, targetInfo.Parameters)).ToList();
                            if (candidates.Count != 1) reasons.Add($"游戏目标方法匹配数为 {candidates.Count}");
                            else
                            {
                                var resolved = candidates[0];
                                resolvedTarget = resolved.FullName;
                                if (resolved.HasGenericParameters) reasons.Add("泛型目标方法不支持");
                                if (!resolved.HasBody) reasons.Add("目标方法没有 IL 方法体");
                                if (kind == "Postfix" && HasProtectedReturn(resolved))
                                    reasons.Add("return 位于异常处理区域，当前 Postfix 单出口不支持");
                            }
                        }
                    }

                    string compatibility;
                    if (reasons.Count > 0) compatibility = "unsupported";
                    else compatibility = hook.IsPublic && IsPubliclyCallable(type)
                        ? "supported"
                        : "requires-hook-promotion";

                    result.Add(new PatchAudit(
                        type.FullName, hook.Name, kind,
                        targetInfo.Type, targetInfo.Method, targetInfo.Parameters,
                        targetInfo.MethodType, dynamicTarget, resolvedTarget,
                        hook.IsPublic, hook.IsStatic,
                        hook.Parameters.Select(x => $"{x.ParameterType.FullName} {x.Name}").ToArray(),
                        specialParameters, compatibility, reasons.ToArray()));
                }
            }
        }
        return result;
    }

    private static TargetInfo ReadTarget(TypeDefinition type, MethodDefinition method)
    {
        TypeReference? targetType = null;
        string? targetMethod = null;
        List<string>? parameters = null;
        string methodType = "Normal";

        foreach (var attribute in type.CustomAttributes.Concat(method.CustomAttributes)
                     .Where(x => x.AttributeType.FullName == PatchAttribute))
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Type.FullName == "System.Type" && argument.Value is TypeReference tr)
                    targetType = tr;
                else if (argument.Type.FullName == "System.String" && argument.Value is string name)
                    targetMethod = name;
                else if (argument.Type is ArrayType { ElementType.FullName: "System.Type" }
                         && argument.Value is CustomAttributeArgument[] types)
                    parameters = types.Select(x => ((TypeReference)x.Value).FullName).ToList();
                else if (argument.Type.FullName == "HarmonyLib.MethodType")
                    methodType = MethodTypeName(Convert.ToInt32(argument.Value));
            }
        }
        return new TargetInfo(targetType?.FullName, targetMethod, parameters, methodType);
    }

    private static object[] FindInitializers(AssemblyDefinition mod) => mod.MainModule.GetTypes()
        .SelectMany(type => type.CustomAttributes
            .Where(attribute => attribute.AttributeType.Name == "ModInitializerAttribute")
            .Select(attribute => (object)new
            {
                Type = type.FullName,
                Method = attribute.ConstructorArguments.FirstOrDefault().Value?.ToString()
            }))
        .ToArray();

    private static object[] AnalyzeAotRisks(AssemblyDefinition mod)
    {
        var risks = new List<object>();
        foreach (var method in mod.MainModule.GetTypes().SelectMany(x => x.Methods))
        {
            if (method.IsPInvokeImpl)
                risks.Add(new { Category = "PInvoke", Location = method.FullName, Detail = method.PInvokeInfo?.Module.Name });
            if (!method.HasBody) continue;
            foreach (var called in method.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>())
            {
                string? category = called.DeclaringType.Namespace switch
                {
                    "System.Reflection.Emit" => "Reflection.Emit",
                    _ when called.DeclaringType.FullName == "System.Reflection.Assembly" && called.Name.StartsWith("Load") => "Assembly.Load",
                    _ when called.DeclaringType.FullName == "System.Runtime.Loader.AssemblyLoadContext" && called.Name.StartsWith("Load") => "AssemblyLoadContext.Load",
                    _ when called.Name is "MakeGenericType" or "MakeGenericMethod" => "RuntimeGenericInstantiation",
                    _ when called.DeclaringType.FullName == "System.Activator" && called.Name == "CreateInstance" => "ReflectionActivation",
                    _ when called.DeclaringType.FullName is "System.Type" or "System.Reflection.Assembly"
                           && (called.Name.StartsWith("GetType") || called.Name.StartsWith("GetMethod")
                               || called.Name.StartsWith("GetField") || called.Name.StartsWith("GetPropert")) => "ReflectionMetadata",
                    _ when called.DeclaringType.FullName == "System.Reflection.CustomAttributeExtensions" => "ReflectionMetadata",
                    _ when called.DeclaringType.Namespace == "HarmonyLib" && called.Name.Contains("Patch") => "RuntimeHarmonyPatching",
                    _ => null
                };
                if (category != null)
                    risks.Add(new { Category = category, Location = method.FullName, Detail = called.FullName });
            }
        }
        return risks.DistinctBy(x => JsonSerializer.Serialize(x)).ToArray();
    }

    private static bool SignatureMatches(MethodDefinition method, List<string> parameters) =>
        method.Parameters.Count == parameters.Count
        && method.Parameters.Select((x, i) => x.ParameterType.FullName == parameters[i]
            || x.ParameterType.Name == parameters[i]).All(x => x);

    private static bool HasProtectedReturn(MethodDefinition method) => method.HasBody && method.Body.HasExceptionHandlers
        && method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).Any(ret => method.Body.ExceptionHandlers.Any(handler =>
            InRange(method, ret, handler.TryStart, handler.TryEnd)
            || InRange(method, ret, handler.HandlerStart, handler.HandlerEnd)));

    private static bool InRange(MethodDefinition method, Instruction instruction, Instruction? start, Instruction? end)
    {
        if (start == null) return false;
        int index = method.Body.Instructions.IndexOf(instruction);
        int first = method.Body.Instructions.IndexOf(start);
        int last = end == null ? method.Body.Instructions.Count : method.Body.Instructions.IndexOf(end);
        return index >= first && index < last;
    }

    private static bool IsPubliclyCallable(TypeDefinition type)
    {
        for (TypeDefinition? current = type; current != null; current = current.DeclaringType)
            if (current.IsNested ? !current.IsNestedPublic : !current.IsPublic) return false;
        return true;
    }

    private static bool HasAttribute(ICustomAttributeProvider provider, string name) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(x => x.AttributeType.FullName == name);

    private static IEnumerable<string> GetPatchKinds(TypeDefinition type, MethodDefinition method)
    {
        var explicitKinds = PatchKinds.Where(x => HasAttribute(method, x.Key)).Select(x => x.Value).ToArray();
        if (explicitKinds.Length > 0) return explicitKinds;

        bool conventionPatchMethod = HasAttribute(type, PatchAttribute) || HasAttribute(method, PatchAttribute);
        return conventionPatchMethod && method.Name is "Prefix" or "Postfix" or "Transpiler" or "Finalizer"
            ? [method.Name]
            : [];
    }

    private static string MethodTypeName(int value) => value switch
    {
        0 => "Normal", 1 => "Getter", 2 => "Setter", 3 => "Constructor",
        4 => "StaticConstructor", 5 => "Enumerator", 6 => "Async", _ => $"Unknown({value})"
    };

    private sealed record TargetInfo(string? Type, string? Method, List<string>? Parameters, string MethodType);
    private sealed record PatchAudit(
        string PatchType, string HookMethod, string Kind,
        string? TargetType, string? TargetMethod, List<string>? TargetParameters,
        string MethodType, bool DynamicTarget, string? ResolvedTarget,
        bool HookIsPublic, bool HookIsStatic, string[] HookParameters,
        string[] SpecialParameters, string Compatibility, string[] Reasons);
}
