using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DynamicCampaignJapanese;
using HarmonyLib;
using Newtonsoft.Json.Linq;

internal static class ShipNameChecks
{
    public static GameShipNames LoadSource(string game, Assembly gameAssembly, List<string> errors)
    {
        FakeLoader.English = ReadIni(Path.Combine(game, "Sea Power_Data", "StreamingAssets", "original", "language_en", "vessel_names.ini"));
        FakeLoader.Japanese = ReadIni(Path.Combine(game, "Sea Power_Data", "StreamingAssets", "original", "language_ja", "vessel_names.ini"));
        FakeLoader.Requests.Clear();
        var names = new GameShipNames(typeof(FakeLoader), typeof(FakeIni), typeof(FakeOptions));
        foreach (var type in new[] { ("SeaPower.LocalizationLoader", "OpenVirtualLocalizationFile", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }),
            ("SeaPower.IniHandler", "getNamesOfAllSections", Type.EmptyTypes),
            ("SeaPower.IniHandler", "GetAllKeysOfSection", new[] { typeof(string) }),
            ("SeaPower.IniHandler", "readValue", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }) })
            if (gameAssembly.GetType(type.Item1, true)!.GetMethod(type.Item2, type.Item3) == null)
                errors.Add("The installed game's vessel-name API is incompatible: " + type.Item1 + "::" + type.Item2);
        if (gameAssembly.GetType("SeaPower.OptionsManager", true)!.GetProperty("NatoUnitNamesEnabled", BindingFlags.Public | BindingFlags.Static) == null)
            errors.Add("The installed game's NATO naming option is unavailable.");
        if (!FakeLoader.Requests.SequenceEqual(new[] { "language_en/vessel_names/vessel_names/True", "language_ja/vessel_names/vessel_names/True" }))
            errors.Add("Vessel names were not read through both effective localization files.");
        return names;
    }

    public static JObject VerifyDisplay(ShipNameTranslations names, CampaignShipNames campaignNames, Assembly original, List<string> errors)
    {
        Check(names.Ship("usn_aoe_sacramento", null, "Sacramento AOE-1"), "サクラメント AOE-1", "Explicit English hull name", errors);
        Check(names.Ship("usn_aoe_sacramento", "Variant2", "Camden AOE-2"), "カムデン AOE-2", "Named variant", errors);
        Check(names.Ship("usn_aoe_sacramento", "Variant1", "My Sacramento"), "My Sacramento", "Custom hull name", errors);
        Check(names.Ship("unrelated_unit", "Variant1", "Sacramento AOE-1"), "Sacramento AOE-1", "Name in another unit type", errors);
        Check(names.Ship("usn_aoe_sacramento", null, "サクラメント AOE-1"), "サクラメント AOE-1", "Existing Japanese name", errors);
        if (names.Ship("usn_aoe_sacramento", null, null) != null) errors.Add("A missing internal name became a display name.");
        Check(names.Class("usn_aoe_sacramento", "Sacramento", false), "サクラメント級", "Class short name", errors);
        Check(names.Class("usn_dd_spruance_vls", "Spruance", false), FakeLoader.Japanese.readValue("usn_dd_spruance_vls", "Default", "", false).Split(',')[0], "Refitted class", errors);
        Check(names.Class("usn_aoe_sacramento", "Custom class", false), "Custom class", "Unknown class", errors);

        var fixture = Fixture();
        var integration = new ShipDisplayIntegration(fixture.Assembly, names, campaignNames, () => FakeOptions.NatoUnitNamesEnabled);
        var before = (string)fixture.Display.Invoke(null, new[] { fixture.Hull })!;
        typeof(ShipDisplayIntegration).GetField("current", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, integration);
        var translated = Compile(fixture.Display, integration.TranslateInstructions(InstructionReader.Read(fixture.Display), fixture.Display));
        var after = (string)translated.Invoke(null, new[] { fixture.Hull })!;
        Check(before, "Sacramento AOE-1 · Sacramento", "Original display", errors);
        Check(after, "サクラメント AOE-1 · サクラメント級", "Translated display", errors);
        if ((string)fixture.Hull.GetType().GetField("Name")!.GetValue(fixture.Hull)! != "Sacramento AOE-1" ||
            fixture.Hull.GetType().GetField("Variant")!.GetValue(fixture.Hull) != null)
            errors.Add("Rendering a ship changed its internal name or variant.");
        var guarded = original.GetType("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel", true)!.GetMethod("AirGroupText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stateFactory = original.GetType("DynamicCampaignMod.Core.Sim.StateFactory+ShipNames", true)!.GetMethod("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var realIntegration = new ShipDisplayIntegration(original, names, campaignNames, () => false);
        foreach (var method in new[] { guarded, stateFactory })
        {
            var input = InstructionReader.Read(method);
            var output = realIntegration.TranslateInstructions(input, method).ToArray();
            if (!input.Select(instruction => (instruction.opcode, instruction.operand)).SequenceEqual(output.Select(instruction => (instruction.opcode, instruction.operand))))
                errors.Add("Internal name processing was patched: " + MethodIdentity.Of(method));
        }
        var targets = realIntegration.Plan(original);
        if (targets.Contains(guarded) || targets.Contains(stateFactory) || targets.Any(method => !method.DeclaringType!.FullName!.StartsWith("DynamicCampaignMod.UI.", StringComparison.Ordinal)))
            errors.Add("A ship display patch escaped the UI boundary.");
        var nameReads = 0;
        var classReads = 0;
        foreach (var method in targets)
        {
            var input = InstructionReader.Read(method);
            var output = realIntegration.TranslateInstructions(input, method).ToArray();
            if (input.Count != output.Length) { errors.Add("Ship display translation changed instruction count."); continue; }
            for (var index = 0; index < output.Length; index++)
            {
                if (output[index].operand is MethodInfo replacement && replacement.DeclaringType == typeof(ShipDisplayIntegration))
                {
                    if (replacement.Name == nameof(ShipDisplayIntegration.DisplayName)) nameReads++;
                    if (replacement.Name == nameof(ShipDisplayIntegration.DisplayType)) classReads++;
                }
                else if (input[index].opcode != output[index].opcode || !Equals(input[index].operand, output[index].operand))
                    errors.Add("An unrelated ship display instruction changed: " + MethodIdentity.Of(method));
                foreach (var field in new[] { "labels", "blocks" })
                    if (!((IEnumerable)typeof(CodeInstruction).GetField(field)!.GetValue(input[index])!).Cast<object>().SequenceEqual(
                        ((IEnumerable)typeof(CodeInstruction).GetField(field)!.GetValue(output[index])!).Cast<object>()))
                        errors.Add("Ship display translation changed control-flow metadata: " + MethodIdentity.Of(method));
            }
        }
        if (nameReads == 0 || classReads == 0) errors.Add("The actual mod's ship display reads were not translated.");
        VerifyOptionsAndOverrides(errors);
        return new JObject { ["units"] = names.UnitCount, ["originalCaption"] = before, ["translatedCaption"] = after,
            ["displayMethods"] = targets.Count, ["nameReads"] = nameReads, ["classReads"] = classReads,
            ["targets"] = JArray.FromObject(targets.Select(MethodIdentity.Of)), ["internalNameProcessingPreserved"] = true };
    }

    public static JObject VerifySave(Assembly original, object state, object? header, ShipNameTranslations names, CampaignShipNames campaignNames, List<string> errors)
    {
        var write = original.GetType("DynamicCampaignMod.Core.State.CampaignSave", true)!.GetMethod("Write", BindingFlags.Static | BindingFlags.Public)!;
        var before = (string)write.Invoke(null, new[] { state, header })!;
        var integration = new ShipDisplayIntegration(original, names, campaignNames, () => false);
        var changes = new JArray();
        foreach (var force in (IEnumerable)state.GetType().GetField("TaskForces")!.GetValue(state)!)
            foreach (var hull in (IEnumerable)force.GetType().GetField("Ships")!.GetValue(force)!)
            {
                var name = (string?)hull.GetType().GetField("Name")!.GetValue(hull);
                var display = integration.NameOf(hull);
                if (name != display) changes.Add(new JObject { ["internal"] = name, ["display"] = display });
            }
        var after = (string)write.Invoke(null, new[] { state, header })!;
        var unchanged = before == after;
        if (!unchanged) errors.Add("Ship display translation changed serialized save data.");
        if (!changes.Any(change => (string?)change["internal"] == "Sacramento AOE-1" && (string?)change["display"] == "サクラメント AOE-1"))
            errors.Add("The existing save's explicitly named Sacramento was not translated.");
        return new JObject { ["serializedSaveUnchanged"] = unchanged, ["changedDisplayNames"] = changes };
    }

    private static void VerifyOptionsAndOverrides(List<string> errors)
    {
        var english = new Dictionary<string, string> { ["Default"] = "Native class,Native", ["NatoDefault"] = "NATO class,NATO",
            ["Variant1"] = "Same name", ["Variant2"] = "Same name" };
        var japanese = new Dictionary<string, string> { ["Default"] = "原語方式の艦級,Native", ["NatoDefault"] = "NATO方式の艦級,NATO",
            ["Variant1"] = "第一艦", ["Variant2"] = "第二艦" };
        var warnings = new List<string>();
        var names = ShipNameTranslations.Create(new[] { "fixture" }, _ => english.Keys,
            (_, key) => english.GetValueOrDefault(key), (_, key) => japanese.GetValueOrDefault(key), warnings.Add);
        Check(names.Class("fixture", "Native", false), "原語方式の艦級", "Native class naming", errors);
        Check(names.Class("fixture", "NATO", true), "NATO方式の艦級", "NATO class naming", errors);
        Check(names.Ship("fixture", null, "Same name"), "Same name", "Ambiguous hull name", errors);
        Check(names.Ship("fixture", "Variant2", "Same name"), "第二艦", "Variant resolves duplicate name", errors);
        if (warnings.Count == 0) errors.Add("An ambiguous ship-name mapping was not reported.");
        var previous = FakeLoader.Japanese.Sections["usn_aoe_sacramento"]["Variant1"];
        try
        {
            FakeLoader.Japanese.Sections["usn_aoe_sacramento"]["Variant1"] = "有効な別MODの艦名 AOE-1,Sacramento";
            var source = new GameShipNames(typeof(FakeLoader), typeof(FakeIni), typeof(FakeOptions));
            Check(source.Translations.Ship("usn_aoe_sacramento", null, "Sacramento AOE-1"), "有効な別MODの艦名 AOE-1", "Effective localization override", errors);
            FakeOptions.NatoUnitNamesEnabled = true;
            if (!source.NatoNames) errors.Add("The current NATO option was not read.");
            FakeOptions.NatoUnitNamesEnabled = false;
            if (source.NatoNames) errors.Add("A changed NATO option remained cached.");
        }
        finally { FakeLoader.Japanese.Sections["usn_aoe_sacramento"]["Variant1"] = previous; FakeOptions.NatoUnitNamesEnabled = false; }
    }

    private static void Check(string? actual, string expected, string label, List<string> errors)
    {
        if (actual != expected) errors.Add(label + ": expected " + expected + ", got " + actual);
    }

    private static (Assembly Assembly, MethodInfo Display, object Hull) Fixture()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ShipDisplayFixture"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixture");
        var hull = module.DefineType("DynamicCampaignMod.Core.State.Hull", TypeAttributes.Public);
        foreach (var name in new[] { "Name", "UnitIni", "Variant" }) hull.DefineField(name, typeof(string), FieldAttributes.Public);
        hull.DefineDefaultConstructor(MethodAttributes.Public);
        var hullType = hull.CreateType()!;
        var catalog = module.DefineType("DynamicCampaignMod.Game.Units.UnitCatalog", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var typeOf = catalog.DefineMethod("TypeOf", MethodAttributes.Public | MethodAttributes.Static, typeof((string, string, string)), new[] { typeof(string) });
        var generator = typeOf.GetILGenerator();
        foreach (var text in new[] { "AOE", "補助艦", "Sacramento" }) generator.Emit(OpCodes.Ldstr, text);
        generator.Emit(OpCodes.Newobj, typeof((string, string, string)).GetConstructor(new[] { typeof(string), typeof(string), typeof(string) })!);
        generator.Emit(OpCodes.Ret);
        var catalogType = catalog.CreateType()!;
        var view = module.DefineType("DynamicCampaignMod.UI.Base.BaseScreenViewModel", TypeAttributes.Public);
        var display = view.DefineMethod("ShipTile", MethodAttributes.Public | MethodAttributes.Static, typeof(string), new[] { hullType });
        generator = display.GetILGenerator();
        generator.DeclareLocal(typeof(string));
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldfld, hullType.GetField("UnitIni")!);
        generator.Emit(OpCodes.Call, catalogType.GetMethod("TypeOf")!);
        generator.Emit(OpCodes.Ldfld, typeof((string, string, string)).GetField("Item3")!);
        generator.Emit(OpCodes.Stloc_0);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldfld, hullType.GetField("Name")!);
        generator.Emit(OpCodes.Ldstr, " · ");
        generator.Emit(OpCodes.Ldloc_0);
        generator.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string), typeof(string) })!);
        generator.Emit(OpCodes.Ret);
        var viewType = view.CreateType()!;
        var instance = Activator.CreateInstance(hullType)!;
        hullType.GetField("Name")!.SetValue(instance, "Sacramento AOE-1");
        hullType.GetField("UnitIni")!.SetValue(instance, "usn_aoe_sacramento");
        return (assembly, viewType.GetMethod("ShipTile")!, instance);
    }

    private static DynamicMethod Compile(MethodInfo original, IEnumerable<CodeInstruction> instructions)
    {
        var result = new DynamicMethod("TranslatedShipTile", original.ReturnType, original.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        var generator = result.GetILGenerator();
        foreach (var local in original.GetMethodBody()!.LocalVariables) generator.DeclareLocal(local.LocalType);
        foreach (var instruction in instructions)
        {
            switch (instruction.operand)
            {
                case null: generator.Emit(instruction.opcode); break;
                case string text: generator.Emit(instruction.opcode, text); break;
                case FieldInfo field: generator.Emit(instruction.opcode, field); break;
                case MethodInfo method: generator.Emit(instruction.opcode, method); break;
                default: throw new InvalidOperationException("Unexpected fixture operand: " + instruction);
            }
        }
        return result;
    }

    private static FakeIni ReadIni(string path)
    {
        var result = new FakeIni();
        Dictionary<string, string>? section = null;
        foreach (var text in File.ReadLines(path))
        {
            var line = text.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                result.Sections[line[1..^1]] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            else if (section != null && !line.StartsWith(';') && !line.StartsWith('#') && !line.StartsWith("//", StringComparison.Ordinal))
            {
                var separator = line.IndexOf('=');
                if (separator > 0) section[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return result;
    }

    public sealed class FakeIni
    {
        public Dictionary<string, Dictionary<string, string>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> getNamesOfAllSections() => Sections.Keys.ToList();
        public List<string> GetAllKeysOfSection(string section) => Sections.TryGetValue(section, out var values) ? values.Keys.ToList() : new();
        public string readValue(string section, string key, string fallback, bool showError) =>
            Sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : fallback;
    }
    public static class FakeLoader
    {
        public static FakeIni English = null!;
        public static FakeIni Japanese = null!;
        public static List<string> Requests { get; } = new();
        public static FakeIni OpenVirtualLocalizationFile(string language, string file, string directory, bool cached)
        {
            Requests.Add(language + "/" + file + "/" + directory + "/" + cached);
            return language == "language_en" ? English : Japanese;
        }
    }
    public static class FakeOptions
    {
        public static bool NatoUnitNamesEnabled { get; set; }
    }
}
