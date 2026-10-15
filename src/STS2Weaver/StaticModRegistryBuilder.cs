using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class StaticModRegistryBuilder
{
    private const string RegistryAssemblyName = "STS2StaticModRegistry";
    private const string RegistryTypeName = "STS2.StaticMods.GeneratedStaticModRegistry";

    public static int Build(string manifestPath, string targetPath, string assemblyDirectory,
        string registryPath, string rootsPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<StaticModManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("静态 Mod manifest 为空");
            if (manifest.SchemaVersion != 1)
                throw new InvalidOperationException($"不支持的 schemaVersion: {manifest.SchemaVersion}（当前仅支持 1）");

            using var target = AssemblyDefinition.ReadAssembly(targetPath);
            ValidateTarget(manifest.Target, target, targetPath);
            var ordered = TopologicalSort(manifest.Mods);
            var loadedMods = LoadAndValidateMods(ordered, assemblyDirectory);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(registryPath))!);
            using var registry = CreateRegistry(target, ordered, loadedMods);
            registry.Write(registryPath);
            WriteRoots(rootsPath, ordered);

            Console.WriteLine($"[OK] 静态 Mod 注册顺序: {string.Join(" -> ", ordered.Select(x => x.Id))}");
            Console.WriteLine($"[OK] 注册程序集: {registryPath}");
            Console.WriteLine($"[OK] trimming roots: {rootsPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[FAIL] 静态 Mod 注册表生成失败: {e.Message}");
            return 1;
        }
    }

    public static int Inject(string targetPath, string registryPath, string outputPath, string? dependencyDirectory)
    {
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(registryPath))!);
            if (dependencyDirectory != null) resolver.AddSearchDirectory(dependencyDirectory);
            var readerParameters = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
            using var registry = AssemblyDefinition.ReadAssembly(registryPath, readerParameters);
            var registryType = registry.MainModule.GetType(RegistryTypeName)
                ?? throw new InvalidOperationException($"注册程序集缺少 {RegistryTypeName}");
            var initialize = registryType.Methods.SingleOrDefault(x => x.Name == "InitializeAll" && x.IsStatic)
                ?? throw new InvalidOperationException("注册程序集缺少 InitializeAll");

            using var target = AssemblyDefinition.ReadAssembly(targetPath, readerParameters);
            var moduleType = target.MainModule.Types.Single(x => x.Name == "<Module>");
            var moduleInitializer = moduleType.Methods.SingleOrDefault(x => x.Name == ".cctor");
            if (moduleInitializer == null)
            {
                moduleInitializer = new MethodDefinition(".cctor",
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    target.MainModule.TypeSystem.Void);
                moduleInitializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                moduleType.Methods.Add(moduleInitializer);
            }

            bool alreadyInjected = moduleInitializer.Body.Instructions.Any(x =>
                x.Operand is MethodReference method
                && method.DeclaringType.FullName == RegistryTypeName
                && method.Name == "InitializeAll");
            if (alreadyInjected)
                throw new InvalidOperationException("目标程序集已包含静态 Mod 注册调用，拒绝重复注入");

            var first = moduleInitializer.Body.Instructions[0];
            moduleInitializer.Body.GetILProcessor().InsertBefore(first,
                Instruction.Create(OpCodes.Call, target.MainModule.ImportReference(initialize)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            target.Write(outputPath);
            Console.WriteLine($"[OK] 已把 {RegistryTypeName}.InitializeAll 注入目标模块初始化器: {outputPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[FAIL] 静态 Mod 注册注入失败: {e.Message}");
            return 1;
        }
    }

    public static int VerifyInputs(string manifestPath, string modDirectory, string gameDependenciesDirectory,
        string reportPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<StaticModManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("静态 Mod manifest 为空");
            var missing = new List<object>();
            var resolved = new List<object>();
            foreach (var mod in manifest.Mods)
            {
                if (Path.GetFileName(mod.Assembly) != mod.Assembly)
                    throw new InvalidOperationException($"Mod assembly 必须是文件名，不能含路径: {mod.Assembly}");
                string modPath = Path.Combine(modDirectory, mod.Assembly);
                if (!File.Exists(modPath)) throw new InvalidOperationException($"Mod {mod.Id} 程序集不存在: {modPath}");
                using var assembly = AssemblyDefinition.ReadAssembly(modPath);
                foreach (var reference in assembly.MainModule.AssemblyReferences)
                {
                    if (IsProvidedReference(reference.Name, manifest.Target.AssemblyName)) continue;
                    string? file = new[] { modDirectory, gameDependenciesDirectory }
                        .Select(directory => Path.Combine(directory, reference.Name + ".dll"))
                        .FirstOrDefault(File.Exists);
                    if (file == null) missing.Add(new { Mod = mod.Id, Reference = reference.FullName });
                    else resolved.Add(new { Mod = mod.Id, Reference = reference.FullName, File = Path.GetFileName(file) });
                }
            }
            var report = new
            {
                SchemaVersion = 1,
                Compatible = missing.Count == 0,
                Mods = manifest.Mods.Select(x => x.Id).ToArray(),
                Resolved = resolved,
                Missing = missing
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            if (missing.Count > 0)
            {
                foreach (var item in missing) Console.Error.WriteLine($"[ERROR] STATIC1001: {JsonSerializer.Serialize(item)}");
                Console.Error.WriteLine($"[FAIL] 静态 Mod 输入缺少程序集依赖，报告: {reportPath}");
                return 1;
            }
            Console.WriteLine($"[OK] 静态 Mod 输入依赖闭包验证通过: {manifest.Mods.Count} Mod，报告: {reportPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[FAIL] 静态 Mod 输入验证失败: {e.Message}");
            return 1;
        }
    }

    private static AssemblyDefinition CreateRegistry(AssemblyDefinition target, List<StaticModEntry> ordered,
        Dictionary<string, AssemblyDefinition> loadedMods)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(RegistryAssemblyName, new Version(1, 0, 0, 0)),
            RegistryAssemblyName, ModuleKind.Dll);
        assembly.MainModule.RuntimeVersion = target.MainModule.RuntimeVersion;
        var type = new TypeDefinition("STS2.StaticMods", "GeneratedStaticModRegistry",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);

        var state = new FieldDefinition("_state", FieldAttributes.Private | FieldAttributes.Static,
            assembly.MainModule.TypeSystem.Int32);
        var count = new FieldDefinition("_initializationCount", FieldAttributes.Private | FieldAttributes.Static,
            assembly.MainModule.TypeSystem.Int32);
        type.Fields.Add(state);
        type.Fields.Add(count);

        var initialize = new MethodDefinition("InitializeAll",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            assembly.MainModule.TypeSystem.Void);
        type.Methods.Add(initialize);
        var il = initialize.Body.GetILProcessor();
        var start = Instruction.Create(OpCodes.Nop);
        var compareExchange = assembly.MainModule.ImportReference(typeof(Interlocked).GetMethod(
            nameof(Interlocked.CompareExchange), [typeof(int).MakeByRefType(), typeof(int), typeof(int)])!);
        il.Append(Instruction.Create(OpCodes.Ldsflda, state));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Call, compareExchange));
        il.Append(Instruction.Create(OpCodes.Brfalse, start));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(start);

        var getTypeFromHandle = assembly.MainModule.ImportReference(
            typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!);
        foreach (var mod in ordered)
        {
            var modAssembly = loadedMods[mod.Id];
            foreach (var root in mod.RootTypes ?? [])
            {
                var rootType = modAssembly.MainModule.GetType(root)
                    ?? throw new InvalidOperationException($"Mod {mod.Id} 的 root type 不存在: {root}");
                il.Append(Instruction.Create(OpCodes.Ldtoken, assembly.MainModule.ImportReference(rootType)));
                il.Append(Instruction.Create(OpCodes.Call, getTypeFromHandle));
                il.Append(Instruction.Create(OpCodes.Pop));
            }

            if (mod.Initializer != null)
            {
                var initializerType = modAssembly.MainModule.GetType(mod.Initializer.Type)
                    ?? throw new InvalidOperationException($"Mod {mod.Id} 初始化类型不存在: {mod.Initializer.Type}");
                var candidates = initializerType.Methods.Where(x => x.Name == mod.Initializer.Method).ToList();
                if (candidates.Count != 1)
                    throw new InvalidOperationException($"Mod {mod.Id} 初始化方法匹配数为 {candidates.Count}: {mod.Initializer.Type}.{mod.Initializer.Method}");
                var method = candidates[0];
                if (!method.IsPublic || !method.IsStatic || method.Parameters.Count != 0
                    || method.ReturnType.MetadataType != MetadataType.Void)
                    throw new InvalidOperationException($"Mod {mod.Id} 初始化入口必须是 public static void 且无参数: {method.FullName}");
                il.Append(Instruction.Create(OpCodes.Call, assembly.MainModule.ImportReference(method)));
            }

            il.Append(Instruction.Create(OpCodes.Ldsfld, count));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stsfld, count));
        }
        il.Append(Instruction.Create(OpCodes.Ldc_I4_2));
        il.Append(Instruction.Create(OpCodes.Stsfld, state));
        il.Append(Instruction.Create(OpCodes.Ret));

        AddGetter(type, "get_IsInitialized", assembly.MainModule.TypeSystem.Boolean, state, convertToBool: true);
        AddGetter(type, "get_InitializationCount", assembly.MainModule.TypeSystem.Int32, count, convertToBool: false);
        return assembly;
    }

    private static void AddGetter(TypeDefinition type, string name, TypeReference returnType,
        FieldReference field, bool convertToBool)
    {
        var getter = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            returnType);
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field));
        if (convertToBool)
        {
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_2));
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ceq));
        }
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(getter);
        type.Properties.Add(new PropertyDefinition(name[4..], PropertyAttributes.None, returnType) { GetMethod = getter });
    }

    private static Dictionary<string, AssemblyDefinition> LoadAndValidateMods(List<StaticModEntry> ordered,
        string assemblyDirectory)
    {
        var result = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        foreach (var mod in ordered)
        {
            string path = Path.Combine(assemblyDirectory, mod.Assembly);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Mod {mod.Id} 程序集不存在: {path}");
            var assembly = AssemblyDefinition.ReadAssembly(path);
            if (mod.AssemblyVersion != null && assembly.Name.Version.ToString() != mod.AssemblyVersion)
                throw new InvalidOperationException($"Mod {mod.Id} 版本不匹配: manifest={mod.AssemblyVersion}, actual={assembly.Name.Version}");
            result.Add(mod.Id, assembly);
        }
        return result;
    }

    private static List<StaticModEntry> TopologicalSort(List<StaticModEntry> mods)
    {
        var byId = new Dictionary<string, StaticModEntry>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Id)) throw new InvalidOperationException("Mod id 不能为空");
            if (!byId.TryAdd(mod.Id, mod)) throw new InvalidOperationException($"重复 Mod id: {mod.Id}");
        }
        foreach (var mod in mods)
            foreach (string dependency in mod.Dependencies ?? [])
                if (!byId.ContainsKey(dependency))
                    throw new InvalidOperationException($"Mod {mod.Id} 缺少依赖声明: {dependency}");

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<StaticModEntry>();
        void Visit(StaticModEntry mod)
        {
            state.TryGetValue(mod.Id, out int current);
            if (current == 2) return;
            if (current == 1) throw new InvalidOperationException($"Mod 依赖存在环: {mod.Id}");
            state[mod.Id] = 1;
            foreach (string dependency in mod.Dependencies ?? []) Visit(byId[dependency]);
            state[mod.Id] = 2;
            output.Add(mod);
        }
        foreach (var mod in mods) Visit(mod);
        return output;
    }

    private static void ValidateTarget(StaticTarget targetConfig, AssemblyDefinition target, string targetPath)
    {
        if (!string.Equals(targetConfig.AssemblyName, target.Name.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"目标程序集名不匹配: manifest={targetConfig.AssemblyName}, actual={target.Name.Name}");
        if (targetConfig.AssemblyVersion != null && targetConfig.AssemblyVersion != target.Name.Version.ToString())
            throw new InvalidOperationException($"目标版本不匹配: manifest={targetConfig.AssemblyVersion}, actual={target.Name.Version}");
        if (targetConfig.ModuleVersionId != null)
        {
            if (!Guid.TryParse(targetConfig.ModuleVersionId, out var expectedMvid))
                throw new InvalidOperationException($"目标 moduleVersionId 格式无效: {targetConfig.ModuleVersionId}");
            if (target.MainModule.Mvid != expectedMvid)
                throw new InvalidOperationException($"目标 MVID 不匹配: manifest={expectedMvid}, actual={target.MainModule.Mvid}");
        }
        if (targetConfig.Sha256 != null)
        {
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetPath)));
            if (!actual.Equals(targetConfig.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"目标 SHA256 不匹配: manifest={targetConfig.Sha256}, actual={actual}");
        }
    }

    private static void WriteRoots(string path, List<StaticModEntry> ordered)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(path, settings);
        writer.WriteStartElement("linker");
        foreach (var mod in ordered.Where(x => x.RootTypes is { Count: > 0 }))
        {
            writer.WriteStartElement("assembly");
            writer.WriteAttributeString("fullname", Path.GetFileNameWithoutExtension(mod.Assembly));
            foreach (string root in mod.RootTypes!)
            {
                writer.WriteStartElement("type");
                writer.WriteAttributeString("fullname", root);
                writer.WriteAttributeString("preserve", "all");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static bool IsProvidedReference(string name, string targetAssemblyName) =>
        name == targetAssemblyName || name is "GodotSharp" or "0Harmony"
        || name is "mscorlib" or "netstandard" or "System" or "System.Core"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private sealed record StaticModManifest(int SchemaVersion, StaticTarget Target, List<StaticModEntry> Mods);
    private sealed record StaticTarget(string AssemblyName, string? AssemblyVersion = null,
        string? ModuleVersionId = null, string? Sha256 = null);
    private sealed record StaticModEntry(string Id, string Assembly, List<string>? Dependencies = null,
        StaticInitializer? Initializer = null, List<string>? RootTypes = null, string? AssemblyVersion = null);
    private sealed record StaticInitializer(string Type, string Method);
}
