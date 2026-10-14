using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using DynamicCampaignJapanese;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static string Game = null!;
    private static string AnchorChainDirectory = null!;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly List<string> Errors = new();

    public static int Main(string[] arguments)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (arguments.Length != 5) throw new ArgumentException("Pass the project, Sea Power, Anchor Chain, original mod and baseline directories.");
            var root = Path.GetFullPath(arguments[0]);
            Game = Path.GetFullPath(arguments[1]);
            AnchorChainDirectory = Path.GetFullPath(arguments[2]);
            var originalDirectory = Path.GetFullPath(arguments[3]);
            var baselineDirectory = Path.GetFullPath(arguments[4]);
            AssemblyLoadContext.Default.Resolving += ResolveGameAssembly;
            var startupChecks = CompatibilityChecks.VerifyStartup(Errors);
            var activationChecks = VerifyModActivation();
            var catalog = TranslationCatalog.Load(Path.Combine(root, "localization", "translation-data.json"));
            var integration = new OriginalModIntegration(catalog);
            var original = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(originalDirectory, "DynamicCampaignMod.dll"));
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Seapower-Scripts");
            var shipNames = ShipNameChecks.LoadSource(Game, gameAssembly, Errors);
            var shipDisplays = ShipNameChecks.VerifyDisplay(shipNames.Translations, original, Errors);
            var oldAssembly = Assembly.LoadFile(Path.Combine(baselineDirectory, "installed-package", "DynamicCampaignJapanese.dll"));
            AssemblyLoadContext.GetLoadContext(oldAssembly)!.Resolving += ResolveGameAssembly;
            var legacy = oldAssembly.GetType("BungalowChinese.Runtime", true)!;
            legacy.GetMethod("LoadData", All)!.Invoke(null, new object[] { Path.Combine(baselineDirectory, "installed-package") });
            var baseline = JObject.Parse(File.ReadAllText(Path.Combine(baselineDirectory, "runtime-output.json")));

            var markup = new MarkupTranslator(catalog);
            var resources = (JArray)baseline["Resources"]!;
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "xaml-generated"));
            foreach (var resource in resources)
            {
                var expected = ContextualMessageChecks.ExpectedMarkup((string)resource["Name"]!, (string)resource["Translated"]!);
                var actual = markup.Translate((string)resource["Original"]!);
                File.WriteAllText(Path.Combine(root, "artifacts", "xaml-generated", (string)resource["Name"]!), actual);
                var left = System.Xml.Linq.XDocument.Parse(expected);
                var right = System.Xml.Linq.XDocument.Parse(actual);
                if (!System.Xml.Linq.XNode.DeepEquals(left, right)) Errors.Add("XAML mismatch: " + resource["Name"]);
            }

            var campaign = new CampaignTranslator(catalog).Translate((string)baseline["OriginalCampaign"]!);
            var expectedCampaign = JObject.Parse((string)baseline["TranslatedCampaign"]!);
            var actualCampaign = JObject.Parse(campaign);
            if (!JToken.DeepEquals(expectedCampaign, actualCampaign)) Errors.Add("Campaign translation mismatch.");
            File.WriteAllText(Path.Combine(root, "artifacts", "translated-campaign.json"), actualCampaign.ToString(Formatting.Indented));

            var plan = integration.Plan(original);
            var oldTranspiler = legacy.GetMethod("TranslateIL", All)!;
            var instructionsCompared = 0;
            foreach (var method in plan)
            {
                List<CodeInstruction> source;
                try { source = InstructionReader.Read(method); }
                catch (Exception error) { throw new InvalidOperationException("Cannot inspect " + MethodIdentity.Of(method), error); }
                var left = source.Select(instruction => new CodeInstruction(instruction)).ToList();
                var right = source.Select(instruction => new CodeInstruction(instruction)).ToList();
                var expected = ((IEnumerable<CodeInstruction>)oldTranspiler.Invoke(null, new object[] { left, method })!).ToArray();
                expected = ContextualMessageChecks.ExpectedInstructions(method, source, expected);
                var actual = integration.TranslateInstructions(right, method).ToArray();
                if (MethodIdentity.Of(method) == AircraftMessageTranslation.Target)
                {
                    expected = ContextualMessageChecks.NormalizeAircraftMessage(expected, false);
                    actual = ContextualMessageChecks.NormalizeAircraftMessage(actual, true);
                }
                if (expected.Length != actual.Length)
                {
                    Errors.Add("Instruction count mismatch: " + MethodIdentity.Of(method));
                    continue;
                }
                for (var index = 0; index < expected.Length; index++)
                {
                    instructionsCompared++;
                    var a = expected[index];
                    var b = actual[index];
                    if (a.opcode != b.opcode || !Equals(NormalizeOperand(a.operand), NormalizeOperand(b.operand)) ||
                        !MetadataEqual(a, b, "labels") || !MetadataEqual(a, b, "blocks"))
                        Errors.Add("Instruction mismatch: " + MethodIdentity.Of(method) + " / " + index + " / " + a + " / " + b);
                }
            }

            var cacheLeaves = CacheLeaves(original, catalog);
            var before = cacheLeaves.Select(leaf => leaf.Read()).ToArray();
            legacy.GetMethod("TranslateCachedDisplayData", All)!.Invoke(null, new object[] { original });
            var expectedCache = cacheLeaves.Select(leaf => leaf.Read()).ToArray();
            for (var index = 0; index < cacheLeaves.Count; index++) cacheLeaves[index].Write(before[index]);
            var cachedChanges = CachedDisplayTranslations.Apply(original, catalog);
            for (var index = 0; index < cacheLeaves.Count; index++)
                if (cacheLeaves[index].Read() != expectedCache[index]) Errors.Add("Cached display mismatch: " + cacheLeaves[index].Name);

            var bindings = new JArray();
            foreach (var pair in new[] { ("DynamicCampaignMod.UI.Strategy.FleetGroupRow", "set_Speed", "Speed"), ("DynamicCampaignMod.UI.Strategy.StockBaseRow", "set_Weapons", "Weapons") })
            {
                var method = original.GetType(pair.Item1, true)!.GetMethod(pair.Item2, All)!;
                var output = integration.TranslateInstructions(InstructionReader.Read(method), method).ToArray();
                var values = output.Where(instruction => instruction.opcode == OpCodes.Ldstr).Select(instruction => (string)instruction.operand).ToArray();
                if (!values.Contains(pair.Item3, StringComparer.Ordinal)) Errors.Add("Binding property was translated: " + pair.Item3);
                bindings.Add(new JObject { ["method"] = MethodIdentity.Of(method), ["preserved"] = pair.Item3 });
            }

            var definitionType = original.GetType("DynamicCampaignMod.Core.Data.CampaignDefinition", true)!;
            var definition = JsonConvert.DeserializeObject(campaign, definitionType)!;
            var readSave = original.GetType("DynamicCampaignMod.Core.State.CampaignSave", true)!.GetMethod("Read", All)!;
            var saves = new JArray();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(baselineDirectory, "saves"), "*.json", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta.json", StringComparison.Ordinal)) continue;
                var args = new object?[] { File.ReadAllText(file), definition, null, null };
                var state = readSave.Invoke(null, args);
                if (state == null || args[3] != null) Errors.Add("Save failed to load: " + Path.GetFileName(file) + " / " + args[3]);
                var shipSaveCheck = state == null ? null : ShipNameChecks.VerifySave(original, state, args[2], shipNames.Translations, Errors);
                saves.Add(new JObject
                {
                    ["name"] = Path.GetFileName(file), ["loaded"] = state != null, ["error"] = args[3]?.ToString(),
                    ["taskForces"] = state == null ? 0 : ((ICollection)state.GetType().GetField("TaskForces")!.GetValue(state)!).Count,
                    ["campaignId"] = definitionType.GetProperty("Id")!.GetValue(definition)!.ToString(),
                    ["shipDisplayChecks"] = shipSaveCheck
                });
            }
            if (saves.Count == 0) Errors.Add("No existing save was checked.");
            var contextualMessages = ContextualMessageChecks.VerifyAircraft(original, integration, catalog, Errors);
            var compatibilityChecks = CompatibilityChecks.VerifyPartialTranslation(root, Errors);

            var report = new JObject
            {
                ["passed"] = Errors.Count == 0, ["errors"] = JArray.FromObject(Errors),
                ["xamlResourcesCompared"] = resources.Count, ["campaignMatchesBaseline"] = JToken.DeepEquals(expectedCampaign, actualCampaign),
                ["methodsCompared"] = plan.Count, ["instructionsCompared"] = instructionsCompared,
                ["cachedLabelsChanged"] = cachedChanges, ["cachedDisplayLeavesCompared"] = cacheLeaves.Count,
                ["bindings"] = bindings, ["saves"] = saves, ["gameRenderingTested"] = false
            };
            report["modActivationChecks"] = activationChecks;
            report["startupChecks"] = startupChecks;
            report["compatibilityChecks"] = compatibilityChecks;
            report["shipDisplayChecks"] = shipDisplays;
            report["contextualMessageChecks"] = contextualMessages;
            File.WriteAllText(Path.Combine(root, "artifacts", "verification-report.json"), report.ToString(Formatting.Indented));
            Console.WriteLine(report.ToString(Formatting.None));
            return Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    private static JObject VerifyModActivation()
    {
        var directory = new ActivationDirectory { DirectoryInfo = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "DynamicCampaignJapanese-verification")), IsChecked = true };
        ActivationManager.Instance = new ActivationManager();
        ActivationManager.Instance.Directories.Add(directory);
        if (!ModActivation.IsEnabled(typeof(ActivationManager), directory.DirectoryInfo.FullName + Path.DirectorySeparatorChar))
            Errors.Add("An enabled mod was rejected.");
        directory.IsChecked = false;
        if (ModActivation.IsEnabled(typeof(ActivationManager), directory.DirectoryInfo.FullName.ToUpperInvariant()))
            Errors.Add("A disabled mod was enabled.");
        try
        {
            ModActivation.IsEnabled(typeof(ActivationManager), directory.DirectoryInfo.FullName + "-unknown");
            Errors.Add("An unregistered mod directory was silently accepted.");
        }
        catch (InvalidOperationException) { }
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Game, "Sea Power_Data", "Managed", "Seapower-Scripts.dll"));
        var fileManagerType = game.GetType("SeaPower.FileManager", true)!;
        var instance = fileManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        if (instance == null || fileManagerType.GetProperty("Directories") == null)
            Errors.Add("The installed game's file manager API is incompatible.");
        var searchDirectoryType = game.GetType("SeaPower.SearchDirectory", true)!;
        if (searchDirectoryType.GetProperty("DirectoryInfo")?.PropertyType != typeof(DirectoryInfo) ||
            searchDirectoryType.GetProperty("IsChecked")?.PropertyType != typeof(bool))
            Errors.Add("The installed game's mod directory API is incompatible.");
        return new JObject { ["enabled"] = true, ["disabled"] = true, ["unregisteredDirectoryRejected"] = true, ["inheritedInstanceResolved"] = instance != null };
    }

    public class ActivationSingleton
    {
        public static ActivationManager Instance { get; set; } = null!;
    }

    public sealed class ActivationManager : ActivationSingleton
    {
        public List<ActivationDirectory> Directories { get; } = new();
    }

    public sealed class ActivationDirectory
    {
        public DirectoryInfo DirectoryInfo { get; set; } = null!;
        public bool IsChecked { get; set; }
    }

    private static Assembly? ResolveGameAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        if (loaded != null) return loaded;
        foreach (var directory in new[] { Path.Combine(Game, "Sea Power_Data", "Managed"), Path.Combine(Game, "BepInEx", "core"), AnchorChainDirectory })
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        return null;
    }

    private static object? NormalizeOperand(object? operand)
    {
        if (operand is MethodInfo method)
        {
            if (method.DeclaringType?.FullName == "BungalowChinese.Runtime" && method.Name == "TranslateXaml") return "localization:markup";
            if (method.DeclaringType?.FullName == "DynamicCampaignJapanese.OriginalModIntegration" && method.Name == "TranslateMarkup") return "localization:markup";
            if (method.DeclaringType?.FullName is "BungalowChinese.Runtime" or "DynamicCampaignJapanese.OriginalModIntegration" && method.Name == "ReadCampaign") return "localization:campaign";
        }
        return operand;
    }

    private static bool MetadataEqual(CodeInstruction left, CodeInstruction right, string name)
    {
        var field = typeof(CodeInstruction).GetField(name)!;
        var a = ((IEnumerable)field.GetValue(left)!).Cast<object>().ToArray();
        var b = ((IEnumerable)field.GetValue(right)!).Cast<object>().ToArray();
        return a.SequenceEqual(b);
    }

    private sealed class Leaf
    {
        public string Name { get; }
        private readonly Func<string?> read;
        private readonly Action<string?> write;
        public Leaf(string name, Func<string?> read, Action<string?> write) => (Name, this.read, this.write) = (name, read, write);
        public string? Read() => read();
        public void Write(string? value) => write(value);
    }

    private static List<Leaf> CacheLeaves(Assembly assembly, TranslationCatalog catalog)
    {
        var leaves = new List<Leaf>();
        foreach (var signature in catalog.Methods.Keys.Where(key => key.EndsWith("::.cctor()", StringComparison.Ordinal)))
        {
            var type = assembly.GetType(signature.Substring(0, signature.Length - "::.cctor()".Length), true)!;
            foreach (var field in type.GetFields(All | BindingFlags.DeclaredOnly).Where(field => field.IsStatic))
            {
                var value = field.GetValue(null);
                if (value is string[] strings)
                    for (var i = 0; i < strings.Length; i++)
                    {
                        var index = i;
                        leaves.Add(new Leaf(field.Name + "[" + i + "]", () => strings[index], text => strings[index] = text!));
                    }
                else if (value is Array array)
                    for (var i = 0; i < array.Length; i++)
                    {
                        var index = i;
                        var item = array.GetValue(index);
                        if (item == null) continue;
                        foreach (var member in item.GetType().GetFields(All).Where(member => !member.IsStatic && member.FieldType == typeof(string)))
                            leaves.Add(new Leaf(field.Name + "[" + i + "]." + member.Name,
                                () => (string?)member.GetValue(array.GetValue(index)), text =>
                                {
                                    var box = array.GetValue(index)!;
                                    member.SetValue(box, text);
                                    array.SetValue(box, index);
                                }));
                    }
                else if (value != null && value.GetType().Assembly == assembly)
                    foreach (var member in value.GetType().GetFields(All).Where(member => !member.IsStatic && member.FieldType == typeof(string)))
                        leaves.Add(new Leaf(field.Name + "." + member.Name, () => (string?)member.GetValue(value), text => member.SetValue(value, text)));
            }
        }
        return leaves;
    }
}
