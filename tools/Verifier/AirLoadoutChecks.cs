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
        var factories = integration.Plan(original, warnings.Add);
        if (factories.Count != 2 || warnings.Count != 0) errors.Add("Both airfield and carrier loadout displays were not found.");
        var cases = new JArray();
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
            var selection = factory.DeclaringType.GetMethods(All).Single(method =>
                method.Name.StartsWith("<UpdateLoadouts>b__", StringComparison.Ordinal) && method.ReturnType == typeof(bool));
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
        if (warnings.Count != factories.Count) errors.Add("Unrecognized loadout displays were not reported.");
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
        return new JObject { ["views"] = cases, ["effectiveLocalizationOverride"] = true,
            ["unknownLoadoutNamePreserved"] = true, ["unrecognizedConstructionReported"] = true };
    }

    private static bool SameInstructions(IEnumerable<CodeInstruction> left, IEnumerable<CodeInstruction> right) =>
        left.Select(instruction => (instruction.opcode, instruction.operand)).SequenceEqual(right.Select(instruction => (instruction.opcode, instruction.operand)));

    private static DynamicMethod Compile(MethodInfo factory, IEnumerable<CodeInstruction> instructions)
    {
        var result = new DynamicMethod("TranslatedLoadoutPicker", factory.ReturnType,
            new[] { factory.DeclaringType!, factory.GetParameters()[0].ParameterType }, factory.Module, true);
        var generator = result.GetILGenerator();
        foreach (var instruction in instructions)
            switch (instruction.operand)
            {
                case null: generator.Emit(instruction.opcode); break;
                case string text: generator.Emit(instruction.opcode, text); break;
                case FieldInfo field: generator.Emit(instruction.opcode, field); break;
                case MethodInfo method: generator.Emit(instruction.opcode, method); break;
                case ConstructorInfo constructor: generator.Emit(instruction.opcode, constructor); break;
                default: throw new InvalidOperationException("Unexpected loadout picker operand: " + instruction);
            }
        return result;
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
