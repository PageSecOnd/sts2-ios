using Mono.Cecil;

/// <summary>
/// 将无需运行 Harmony 的声明式 Prefix/Postfix 子集转换为现有 manifest 模型。
/// 此处只读特性元数据；不会加载 Mod，也不会执行 TargetMethod 等动态代码。
/// </summary>
static class HarmonyPatchDiscovery
{
    private const string HarmonyPatch = "HarmonyLib.HarmonyPatch";
    private const string HarmonyPrefix = "HarmonyLib.HarmonyPrefix";
    private const string HarmonyPostfix = "HarmonyLib.HarmonyPostfix";
    private const string HarmonyFinalizer = "HarmonyLib.HarmonyFinalizer";
    private const string HarmonyTranspiler = "HarmonyLib.HarmonyTranspiler";
    private const string HarmonyTargetMethod = "HarmonyLib.HarmonyTargetMethod";
    private const string HarmonyTargetMethods = "HarmonyLib.HarmonyTargetMethods";

    public static Manifest Discover(AssemblyDefinition modAssembly, string? selectedHookType = null, string? selectedHookMethod = null)
    {
        var patches = new List<PatchEntry>();
        var errors = new List<string>();

        foreach (var type in modAssembly.MainModule.GetTypes())
        {
            bool isPatchContainer = HasAttribute(type, HarmonyPatch);
            foreach (var method in type.Methods)
            {
                if (selectedHookType != null
                    && (type.FullName != selectedHookType || method.Name != selectedHookMethod))
                    continue;
                bool conventionPatchMethod = isPatchContainer || HasAttribute(method, HarmonyPatch);
                bool isTranspiler = HasAttribute(method, HarmonyTranspiler)
                    || (conventionPatchMethod && method.Name == "Transpiler");
                bool isFinalizer = HasAttribute(method, HarmonyFinalizer)
                    || (conventionPatchMethod && method.Name == "Finalizer");
                if (isTranspiler)
                    errors.Add($"{type.FullName}.{method.Name}: Transpiler 依赖任意 IL 变换，Phase 1 不支持；需人工改写为静态 Cecil 补丁");
                if (isFinalizer)
                    errors.Add($"{type.FullName}.{method.Name}: Finalizer 依赖异常路径语义，Phase 1 不支持；需人工适配异常处理块");
                if (HasAttribute(method, HarmonyTargetMethod) || HasAttribute(method, HarmonyTargetMethods)
                    || (isPatchContainer && method.Name is "TargetMethod" or "TargetMethods"))
                    errors.Add($"{type.FullName}.{method.Name}: 动态 TargetMethod/TargetMethods 不会在构建期执行；请改用声明式 [HarmonyPatch(typeof(...), nameof(...))]");

                bool isPrefix = HasAttribute(method, HarmonyPrefix)
                    || (conventionPatchMethod && method.Name == "Prefix");
                bool isPostfix = HasAttribute(method, HarmonyPostfix)
                    || (conventionPatchMethod && method.Name == "Postfix");
                if (!isPrefix && !isPostfix) continue;
                if (isPrefix && isPostfix)
                {
                    errors.Add($"{type.FullName}.{method.Name}: 同一方法不能同时标记 HarmonyPrefix 和 HarmonyPostfix");
                    continue;
                }

                try
                {
                    var descriptor = ReadTarget(type, method);
                    patches.Add(new PatchEntry(
                        descriptor.TargetType,
                        descriptor.TargetMethod,
                        isPrefix ? "prefix" : "postfix",
                        type.FullName,
                        method.Name,
                        descriptor.TargetParams));
                }
                catch (Exception e)
                {
                    errors.Add($"{type.FullName}.{method.Name}: {e.Message}");
                }
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Harmony 分析发现不兼容补丁:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(x => "  - " + x)));
        if (patches.Count == 0)
            throw new InvalidOperationException(selectedHookType == null
                ? "未发现带 [HarmonyPrefix] 或 [HarmonyPostfix] 的方法"
                : $"未发现所选 Harmony hook: {selectedHookType}.{selectedHookMethod}");

        // 先织入所有 Prefix，再织入 Postfix。这样 bool Prefix 的提前 ret 也会被随后建立的
        // Postfix 单出口捕获，保持 Harmony 的“跳过原方法但仍执行 Postfix”语义。
        patches = patches.OrderBy(x => x.Kind == "prefix" ? 0 : 1).ToList();
        Console.WriteLine($"[OK] Harmony 元数据分析: {patches.Count} 个可静态转换的 Prefix/Postfix");
        return new Manifest(patches);
    }

    private static TargetDescriptor ReadTarget(TypeDefinition patchType, MethodDefinition patchMethod)
    {
        var attributes = patchType.CustomAttributes.Concat(patchMethod.CustomAttributes)
            .Where(x => x.AttributeType.FullName == HarmonyPatch)
            .ToList();
        if (attributes.Count == 0)
            throw new InvalidOperationException("缺少 HarmonyPatch 目标声明");

        TypeReference? targetType = null;
        string? targetMethod = null;
        List<string>? targetParams = null;

        foreach (var attribute in attributes)
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Type.FullName == "System.Type" && argument.Value is TypeReference tr)
                    targetType = tr;
                else if (argument.Type.FullName == "System.String" && argument.Value is string name)
                    targetMethod = name;
                else if (argument.Type is ArrayType { ElementType.FullName: "System.Type" }
                         && argument.Value is CustomAttributeArgument[] typeArguments)
                    targetParams = typeArguments.Select(ReadTypeName).ToList();
            }
        }

        if (targetType == null)
            throw new InvalidOperationException("HarmonyPatch 未提供静态目标类型；运行时类型解析不兼容 NativeAOT 静态织入");
        if (string.IsNullOrWhiteSpace(targetMethod))
            throw new InvalidOperationException("HarmonyPatch 未提供静态目标方法名；构造器/属性访问器与动态解析尚不支持");

        return new TargetDescriptor(targetType.FullName, targetMethod, targetParams);
    }

    private static string ReadTypeName(CustomAttributeArgument argument) =>
        argument.Value is TypeReference type
            ? type.FullName
            : throw new InvalidOperationException("HarmonyPatch 参数类型列表含无法解析的条目");

    private static bool HasAttribute(ICustomAttributeProvider provider, string fullName) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(x => x.AttributeType.FullName == fullName);

    private sealed record TargetDescriptor(string TargetType, string TargetMethod, List<string>? TargetParams);
}
