using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DynamicCampaignJapanese;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class AirLoadoutChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static JObject Verify(string gameDirectory, Assembly game, Assembly original, List<string> errors)
    {
        var ini = new FakeIni();
        var path = Path.Combine(gameDirectory, "Sea Power_Data", "StreamingAssets", "original", "language_ja", "loadout_names.ini");
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
            var separator = line.IndexOf('=');
            if (separator > 0) ini.Values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        FakeLoader.Data = ini;
        var names = new GameLoadoutNames(typeof(FakeLoader), typeof(FakeIni));
        foreach (var api in new[]
        {
            ("SeaPower.LocalizationLoader", "OpenVirtualLocalizationFile", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) }),
            ("SeaPower.IniHandler", "GetAllKeysOfSection", new[] { typeof(string) }),
            ("SeaPower.IniHandler", "readValue", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) })
        })
            if (game.GetType(api.Item1, true)!.GetMethod(api.Item2, api.Item3) == null)
                errors.Add("The installed game's loadout-name API is incompatible: " + api.Item1 + "::" + api.Item2);

        var integration = new AirLoadoutDisplayIntegration(names);
        typeof(AirLoadoutDisplayIntegration).GetField("current", All)!.SetValue(null, integration);
        var warnings = new List<string>();
        var targets = integration.Plan(original, warnings.Add);
        var factories = targets.Where(method => method.ReturnType.FullName == "DynamicCampaignMod.UI.AirBase.PickerItem").ToList();
        var shipyardSelectors = targets.Where(method => method.ReturnType == typeof(string)).ToList();
        if (factories.Count != 2 || warnings.Count != 0) errors.Add("Both airfield and carrier loadout displays were not found.");
        if (shipyardSelectors.Count != 1) errors.Add("The shipyard aircraft loadout display was not found.");
        var cases = new JArray();
        var shipyardCases = new JArray();
        foreach (var factory in factories)
        {
            var aircraft = Activator.CreateInstance(original.GetType("DynamicCampaignMod.Core.Data.AircraftSpec", true)!)!;
            aircraft.GetType().GetField("CruiseRangeNm")!.SetValue(aircraft, 1200d);
            aircraft.GetType().GetField("CruiseKnots")!.SetValue(aircraft, 450d);
            var closure = Activator.CreateInstance(factory.DeclaringType!, true)!;
            factory.DeclaringType!.GetField("spec", All)!.SetValue(closure, aircraft);
            var viewType = factory.DeclaringType.DeclaringType!;
            var view = RuntimeHelpers.GetUninitializedObject(viewType);
            factory.DeclaringType.GetField("<>4__this", All)!.SetValue(closure, view);
            if (viewType.Name == "CarrierAirViewModel")
            {
                var catalogType = original.GetType("DynamicCampaignMod.Core.Data.IUnitCatalog", true)!;
                var proxy = DispatchProxy.Create(catalogType, typeof(CatalogProxy));
                ((CatalogProxy)proxy).Aircraft = aircraft;
                var deckType = original.GetType("DynamicCampaignMod.Core.Sim.CarrierAir", true)!;
                var deck = RuntimeHelpers.GetUninitializedObject(deckType);
                deckType.GetField("_catalog", All)!.SetValue(deck, proxy);
                var simType = original.GetType("DynamicCampaignMod.Core.Sim.CampaignSim", true)!;
                var sim = RuntimeHelpers.GetUninitializedObject(simType);
                simType.GetField("Deck", All)!.SetValue(sim, deck);
                viewType.GetField("_sim", All)!.SetValue(view, sim);
                var picker = original.GetType("DynamicCampaignMod.UI.AirBase.PickerItem", true)!;
                viewType.GetField("_type", All)!.SetValue(view,
                    Activator.CreateInstance(picker, new object?[] { "fixture_aircraft", "Fixture aircraft", null, null }));
            }
            var selection = factory.DeclaringType.GetMethods(All).Single(method =>
                method.Name.StartsWith("<UpdateLoadouts>b__", StringComparison.Ordinal) && method.ReturnType == typeof(bool) &&
                method.GetParameters() is var parameters && parameters.Length == 1 &&
                parameters[0].ParameterType.FullName == "DynamicCampaignMod.UI.AirBase.PickerItem");
            var input = InstructionReader.Read(factory);
            var output = integration.TranslateInstructions(input, factory, warnings.Add).ToArray();
            var translated = Compile(factory, output);
            var checkedCount = 0;
            foreach (var key in ini.Values.Keys.Concat(new[] { "UntranslatedCustomLoadout" }))
            {
                var loadout = Activator.CreateInstance(factory.GetParameters()[0].ParameterType)!;
                loadout.GetType().GetField("Name")!.SetValue(loadout, key);
                loadout.GetType().GetField("ReadyUpMinutes")!.SetValue(loadout, 15);
                loadout.GetType().GetField("CoolDownMinutes")!.SetValue(loadout, 30);
                loadout.GetType().GetField("RangeFactor")!.SetValue(loadout, 0.8d);
                loadout.GetType().GetField("Weapons")!.SetValue(loadout, new Dictionary<string, int> { ["example_missile"] = 2 });
                var before = JsonConvert.SerializeObject(new[] { loadout, aircraft });
                var originalItem = factory.Invoke(closure, new[] { loadout })!;
                var item = translated.Invoke(null, new[] { closure, loadout })!;
                string? Read(object value, string name) => (string?)value.GetType().GetProperty(name)!.GetValue(value);
                var expected = ini.Values.GetValueOrDefault(key, key);
                if (Read(item, "DisplayName") != expected || Read(item, "Key") != key)
                    errors.Add("Loadout display or internal key mismatch: " + viewType.Name + " / " + key);
                foreach (var property in new[] { "Details", "Numbers" })
                    if (Read(item, property) != Read(originalItem, property)) errors.Add("Loadout " + property + " changed: " + key);
                if (JsonConvert.SerializeObject(new[] { loadout, aircraft }) != before)
                    errors.Add("Rendering a loadout changed aircraft or loadout data: " + key);
                viewType.GetField("_loadout", All)!.SetValue(view, originalItem);
                if (!(bool)selection.Invoke(closure, new[] { item })!) errors.Add("A selected loadout was lost after translation: " + key);
                checkedCount++;
            }
            var changed = input.Select(instruction => new CodeInstruction(instruction)).ToArray();
            changed[3] = new CodeInstruction(OpCodes.Ldstr, "custom display");
            var unsupported = integration.TranslateInstructions(changed, factory, warnings.Add).ToArray();
            if (!SameInstructions(changed, unsupported)) errors.Add("An unrecognized loadout display was partially patched.");
            cases.Add(new JObject { ["view"] = viewType.Name, ["loadoutsChecked"] = checkedCount,
                ["internalKeysPreserved"] = true, ["selectionPreserved"] = true, ["aircraftAndLoadoutDataUnchanged"] = true });
        }
        foreach (var selector in shipyardSelectors)
        {
            var closure = Activator.CreateInstance(selector.DeclaringType!, true)!;
            var input = InstructionReader.Read(selector);
            var translated = Compile(selector, integration.TranslateInstructions(input, selector, warnings.Add));
            var checkedCount = 0;
            foreach (var key in ini.Values.Keys.Concat(new[] { "UntranslatedCustomLoadout" }))
            {
                var loadout = Activator.CreateInstance(selector.GetParameters()[0].ParameterType)!;
                loadout.GetType().GetField("Name")!.SetValue(loadout, key);
                loadout.GetType().GetField("ReadyUpMinutes")!.SetValue(loadout, 15);
                loadout.GetType().GetField("Weapons")!.SetValue(loadout, new Dictionary<string, int> { ["example_missile"] = 2 });
                var before = JsonConvert.SerializeObject(loadout);
                var text = (string)translated.Invoke(null, new[] { closure, loadout })!;
                if (text != ini.Values.GetValueOrDefault(key, key)) errors.Add("Shipyard loadout display mismatch: " + key);
                if ((string)selector.Invoke(closure, new[] { loadout })! != key || JsonConvert.SerializeObject(loadout) != before)
                    errors.Add("Rendering a shipyard loadout changed its internal key or data: " + key);
                checkedCount++;
            }
            var changed = input.Select(instruction => new CodeInstruction(instruction)).ToArray();
            changed[1] = new CodeInstruction(OpCodes.Ldstr, "custom display");
            if (!SameInstructions(changed, integration.TranslateInstructions(changed, selector, warnings.Add)))
                errors.Add("An unrecognized shipyard loadout display was partially patched.");
            shipyardCases.Add(new JObject { ["method"] = MethodIdentity.Of(selector), ["loadoutsChecked"] = checkedCount,
                ["internalKeysPreserved"] = true, ["loadoutDataUnchanged"] = true });
        }
        if (warnings.Count != targets.Count) errors.Add("Unrecognized loadout displays were not reported.");
        foreach (var name in new[] { "RefreshDetail", "OrderSelected" })
        {
            var method = original.GetType("DynamicCampaignMod.UI.Strategy.ProcurementViewModel", true)!.GetMethod(name, All)!;
            var input = InstructionReader.Read(method);
            if (!SameInstructions(input, integration.TranslateInstructions(input, method)))
                errors.Add("Shipyard purchase or detail assembly was patched by loadout display translation: " + name);
        }
        foreach (var type in new[] { "DynamicCampaignMod.UI.AirBase.AirBaseViewModel", "DynamicCampaignMod.UI.AirBase.CarrierAirViewModel" })
        {
            var prepare = original.GetType(type, true)!.GetMethod("Prepare", All)!;
            var input = InstructionReader.Read(prepare);
            if (!SameInstructions(input, integration.TranslateInstructions(input, prepare).ToArray()))
                errors.Add("Aircraft preparation was patched by loadout display translation: " + type);
        }
        var previous = ini.Values["AirToAir"];
        ini.Values["AirToAir"] = "別MODの対空兵装名";
        var overridden = new GameLoadoutNames(typeof(FakeLoader), typeof(FakeIni));
        if (overridden.DisplayName("AirToAir") != ini.Values["AirToAir"])
            errors.Add("The effective loadout localization override was ignored.");
        ini.Values["AirToAir"] = previous;
        FakeLoader.Data = new FakeIni();
        try
        {
            _ = new GameLoadoutNames(typeof(FakeLoader), typeof(FakeIni));
            errors.Add("Missing loadout localization data was silently accepted.");
        }
        catch (InvalidOperationException) { }
        finally { FakeLoader.Data = ini; }
        return new JObject { ["views"] = cases, ["shipyardDisplays"] = shipyardCases, ["effectiveLocalizationOverride"] = true,
            ["unknownLoadoutNamePreserved"] = true, ["unrecognizedConstructionReported"] = true };
    }

    private static bool SameInstructions(IEnumerable<CodeInstruction> left, IEnumerable<CodeInstruction> right) =>
        left.Select(instruction => (instruction.opcode, instruction.operand)).SequenceEqual(right.Select(instruction => (instruction.opcode, instruction.operand)));

    private static DynamicMethod Compile(MethodInfo factory, IEnumerable<CodeInstruction> instructions)
    {
        var result = new DynamicMethod("TranslatedLoadoutPicker", factory.ReturnType,
            new[] { factory.DeclaringType!, factory.GetParameters()[0].ParameterType }, factory.Module, true);
        var generator = result.GetILGenerator();
        var locals = factory.GetMethodBody()!.LocalVariables.Select(local => generator.DeclareLocal(local.LocalType, local.IsPinned)).ToArray();
        foreach (var instruction in instructions)
        {
            if (instruction.opcode.OperandType is OperandType.InlineVar or OperandType.ShortInlineVar &&
                instruction.opcode.Name!.Contains("loc", StringComparison.Ordinal))
            {
                generator.Emit(instruction.opcode, locals[Convert.ToInt32(instruction.operand)]);
                continue;
            }
            switch (instruction.operand)
            {
                case null: generator.Emit(instruction.opcode); break;
                case string text: generator.Emit(instruction.opcode, text); break;
                case FieldInfo field: generator.Emit(instruction.opcode, field); break;
                case MethodInfo method: generator.Emit(instruction.opcode, method); break;
                case ConstructorInfo constructor: generator.Emit(instruction.opcode, constructor); break;
                case Type type: generator.Emit(instruction.opcode, type); break;
                case int number: generator.Emit(instruction.opcode, number); break;
                case byte number: generator.Emit(instruction.opcode, number); break;
                case sbyte number: generator.Emit(instruction.opcode, number); break;
                default: throw new InvalidOperationException("Unexpected loadout picker operand: " + instruction);
            }
        }
        return result;
    }

    public class CatalogProxy : DispatchProxy
    {
        public object Aircraft { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "Aircraft" => Aircraft,
            _ => throw new InvalidOperationException("Unexpected catalogue lookup in a loadout display: " + targetMethod)
        };
    }

    public sealed class FakeIni
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> GetAllKeysOfSection(string section) => section == "LoadoutNames" ? Values.Keys.ToList() : new();
        public string readValue(string section, string key, string fallback, bool showError) =>
            section == "LoadoutNames" ? Values.GetValueOrDefault(key, fallback) : fallback;
    }

    public static class FakeLoader
    {
        public static FakeIni Data = null!;
        public static FakeIni OpenVirtualLocalizationFile(string language, string file, string directory, bool cached)
        {
            if (language != "language_ja" || file != "loadout_names" || directory != "loadout_names" || !cached)
                throw new InvalidOperationException("Loadout names did not use the effective Japanese localization data.");
            return Data;
        }
    }
}
