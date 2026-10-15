using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using DynamicCampaignJapanese;
using HarmonyLib;
using MonoMod.Cil;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ContextualMessageChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Footer = "艦艇を選択してから、任務部隊を新設するか、別の部隊へ移動。";
    private static readonly Dictionary<(string Method, string Literal), string> ApprovedChanges = new()
    {
        [("DynamicCampaignMod.UI.TopBar.TopBarViewModel::Refresh()", "{0} · {1:N0}")] = "{0}（名声 {1:N0}",
        [("DynamicCampaignMod.UI.TopBar.TopBarViewModel::Refresh()", "/{0:N0}")] = " / {0:N0}",
        [("DynamicCampaignMod.UI.TopBar.TopBarViewModel::Refresh()", "{0} · victory {1}/{2}")] = "{0}（勝利対象 {1} / {2}）",
        [("DynamicCampaignMod.UI.TopBar.TopBarViewModel::Refresh()", "{0} at sea · {1} airborne")] = "洋上 {0} · 飛行中 {1}",
        [("DynamicCampaignMod.UI.Forces.ForcesViewModel::Update(DynamicCampaignMod.UI.Forces.ForceNode,DynamicCampaignMod.Core.State.TaskForce)", "{0} ship{1}, {2}{3}")] = "{0}隻{1}  {3}  {2}",
        [("DynamicCampaignMod.UI.Forces.ForcesViewModel::Update(DynamicCampaignMod.UI.Forces.ForceNode,DynamicCampaignMod.Core.State.TaskForce)", ", ")] = "",
        [("DynamicCampaignMod.UI.Base.BaseScreenViewModel::SelectionChanged()", "Tick ships to form a new task force or move them to another.")] = Footer,
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::AirGroupText(DynamicCampaignMod.Core.State.Hull)", "Air group in battle: ")] = "搭載機（戦闘時のみ）：",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", "In port, {0} · {1:0} kn")] = "在港（{0}） · {1:0}ノット",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", "At sea, holding · ")] = "洋上 · 待機中 · ",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", "At sea, {0:0} kn, course {1:000}° {2}")] = "洋上 · {0:0}ノット · 針路{1:000}° · {2}",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", "{0}, {1:0} kn {2}")] = "{0} · {1:0}ノット · {2}",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", " · {0} battalion{1}")] = " · {0}個大隊{1}",
        [("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel::Refresh()", " · supplying, about {0:0} h more")] = " · 補給中（残り約{0:0}時間）",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Fill(DynamicCampaignMod.UI.Strategy.FleetGroupRow)", "In port, ")] = "在港（",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Fill(DynamicCampaignMod.UI.Strategy.FleetGroupRow)", "At sea, holding")] = "洋上 · 待機中",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Fill(DynamicCampaignMod.UI.Strategy.FleetGroupRow)", "At sea, ")] = "洋上 · ",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Fill(DynamicCampaignMod.UI.Strategy.FleetGroupRow)", "{0:0} kn")] = "{0:0}ノット",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Fill(DynamicCampaignMod.UI.Strategy.FleetGroupRow)", "{0:0} kn (out of fuel)")] = "{0:0}ノット（燃料切れ）",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Supply(DynamicCampaignMod.Core.State.TaskForce)", " · supplying, about {0:0} h more")] = "） · 補給中（残り約{0:0}時間）",
        [("DynamicCampaignMod.UI.Strategy.FleetViewModel::Supply(DynamicCampaignMod.Core.State.TaskForce)", " · ready")] = "） · 準備完了",
        [("DynamicCampaignMod.UI.Strategy.ProcurementViewModel::Card(DynamicCampaignMod.Core.Data.CatalogueEntry)", "at ")] = "解放階級：",
        [("DynamicCampaignMod.UI.Strategy.ProcurementViewModel::RefreshDetail()", "at ")] = "解放階級：",
        [("DynamicCampaignMod.UI.Strategy.ProcurementViewModel/<>c__DisplayClass132_0::<Group>b__2(System.Linq.IGrouping`2<System.Int32,DynamicCampaignMod.Core.Data.CatalogueEntry>)", "At ")] = "解放階級：",
        [("DynamicCampaignMod.Core.Sim.Story/<>c__DisplayClass17_0::<BuildOpening>g__Place|11(DynamicCampaignMod.Core.State.TaskForce)", "at ")] = "所在：",
        [("DynamicCampaignMod.Core.Sim.Story::LastAction(DynamicCampaignMod.Core.State.CampaignState,System.String,DynamicCampaignMod.Core.Sim.Voice)", " at ")] = "／"
    };

    public static string ExpectedMarkup(string name, string baseline)
    {
        var topBar = name.EndsWith("TopBar.xaml", StringComparison.Ordinal);
        if (!topBar && !name.EndsWith("BaseScreen.xaml", StringComparison.Ordinal)) return baseline;
        var document = XDocument.Parse(baseline);
        foreach (var attribute in document.Descendants().Attributes())
        {
            if (attribute.Name.LocalName == "Content" && attribute.Value == "選択した艦艇で新しい任務部隊を編成") attribute.Value = "選択した艦艇で任務部隊を新設";
            if (topBar && attribute.Name.LocalName == "Text")
                attribute.Value = attribute.Value switch
                {
                    "階級 " => "階級：",
                    " 名声" => "）",
                    "基地 " => "基地：",
                    "部隊 " => "部隊：",
                    _ => attribute.Value
                };
        }
        return document.ToString();
    }

    public static CodeInstruction[] ExpectedInstructions(MethodBase method, IReadOnlyList<CodeInstruction> source, CodeInstruction[] baseline, TranslationCatalog catalog)
    {
        var identity = MethodIdentity.Of(method);
        var revisedText = identity.StartsWith("DynamicCampaignMod.Core.Sim.ContactBriefing::", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.ContactBriefing/", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.UI.Contact.DeploymentViewModel::", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.Story::WeatherNote(", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.Story::Stakes(", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.OrderBriefing::", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.Admiralty::OrderFor(", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.Admiralty::ReportText(", StringComparison.Ordinal) ||
            identity.StartsWith("DynamicCampaignMod.Core.Sim.Admiralty/<>c__DisplayClass110_0::<ReportText>g__Ships|1(", StringComparison.Ordinal);
        catalog.Methods.TryGetValue(identity, out var currentText);
        for (var i = 0; i < source.Count; i++)
        {
            if (source[i].opcode != OpCodes.Ldstr || source[i].operand is not string literal) continue;
            if (ApprovedChanges.TryGetValue((identity, literal), out var text)) baseline[i].operand = text;
            else if (revisedText && currentText != null && currentText.TryGetValue(literal, out text)) baseline[i].operand = text;
        }
        return baseline;
    }

    public static CodeInstruction[] NormalizeAircraftMessage(CodeInstruction[] input, bool translated)
    {
        var instructions = input.Select(instruction => new CodeInstruction(instruction)).ToList();
        var end = instructions.FindIndex(instruction => instruction.operand is MethodInfo method &&
            (translated ? method.DeclaringType == typeof(OriginalModIntegration) && method.Name == nameof(OriginalModIntegration.MissingAircraft) :
                method.DeclaringType == typeof(string) && method.Name == nameof(string.Concat) && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string[])));
        if (end < 0) throw new InvalidOperationException("Aircraft-absence message was not found in the comparison.");
        var start = end;
        while (start > 0 && instructions[start - 1].opcode != OpCodes.Ldarg_S) start--;
        if (start == 0) throw new InvalidOperationException("Aircraft-absence output argument was not found.");
        var marker = new CodeInstruction(OpCodes.Ldstr, "<aircraft-absence-message>");
        instructions[start].MoveLabelsTo(marker);
        instructions[start].MoveBlocksTo(marker);
        instructions.RemoveRange(start, end - start + 1);
        instructions.Insert(start, marker);
        return instructions.ToArray();
    }

    public static JObject VerifyAircraft(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog, List<string> errors)
    {
        var type = original.GetType("DynamicCampaignMod.Core.Sim.AlertLines", true)!;
        var method = type.GetMethod("Add", All)!;
        var output = TranslationILChecks.Read(method, integration).ToArray();
        typeof(OriginalModIntegration).GetField("current", All)!.SetValue(null, integration);
        var translated = Compile(method, output);
        object Create(string name) => Activator.CreateInstance(original.GetType("DynamicCampaignMod.Core." + name, true)!)!;
        var definition = Create("Data.CampaignDefinition");
        var field = Create("Data.BaseDefinition");
        Set(field, "id", "test-airfield");
        Set(field, "name", "テスト at 基地");
        var kind = field.GetType().GetField("kind")!.FieldType;
        Set(field, "kind", Enum.ToObject(kind, 2));
        ((IList)Get(definition, "bases")).Add(field);
        var state = Create("State.CampaignState");
        Set(state, "Definition", definition);
        Set(state, "PlayerSideId", "test-side");
        ((IDictionary)Get(state, "BaseOwner"))["test-airfield"] = "test-side";
        var aircraft = Create("Data.AircraftSpec");
        var loadout = Create("Data.LoadoutSpec");
        Set(loadout, "Name", "test-loadout");
        ((IList)Get(aircraft, "Loadouts")).Add(loadout);
        var proxy = (CatalogProxy)DispatchProxy.Create(original.GetType("DynamicCampaignMod.Core.Data.IUnitCatalog", true)!, typeof(CatalogProxy));
        proxy.Aircraft = aircraft;
        proxy.AircraftName = "F/A-18 at Sea";
        var constructor = type.GetConstructors(All).Single();
        var arguments = new object?[constructor.GetParameters().Length];
        arguments[0] = state;
        arguments[1] = proxy;
        var alerts = constructor.Invoke(arguments);
        var results = new JArray();
        foreach (var scenario in new[]
        {
            (Name: "absent", Base: "test-airfield", Loadout: "test-loadout", Count: 1, Kind: 2),
            (Name: "foreign base", Base: "another-airfield", Loadout: "test-loadout", Count: 1, Kind: 2),
            (Name: "not an airfield", Base: "test-airfield", Loadout: "test-loadout", Count: 1, Kind: 1),
            (Name: "zero aircraft requested", Base: "test-airfield", Loadout: "test-loadout", Count: 0, Kind: 2),
            (Name: "unsupported loadout", Base: "test-airfield", Loadout: "other-loadout", Count: 1, Kind: 2)
        })
        {
            Set(field, "kind", Enum.ToObject(kind, scenario.Kind));
            var before = JsonConvert.SerializeObject(state);
            var originalArguments = new object?[] { scenario.Base, "test-fighter", scenario.Loadout, scenario.Count, null };
            var originalResult = method.Invoke(alerts, originalArguments);
            var translatedArguments = new object?[] { alerts, scenario.Base, "test-fighter", scenario.Loadout, scenario.Count, null };
            proxy.NameRequests = 0;
            var translatedResult = translated.Invoke(null, translatedArguments);
            var text = (string?)translatedArguments[5];
            var expected = scenario.Name == "absent" ? "テスト at 基地にF/A-18 at Seaなし。" : catalog.Methods[AircraftMessageTranslation.Target][(string)originalArguments[4]!];
            if (originalResult != null || translatedResult != null || text != expected)
                errors.Add("Aircraft rejection or display message changed incorrectly: " + scenario.Name + " / " + text);
            if (proxy.NameRequests != (scenario.Name == "absent" ? 1 : 0)) errors.Add("Aircraft display names were read outside the absence branch: " + scenario.Name);
            if (before != JsonConvert.SerializeObject(state)) errors.Add("Rendering an aircraft rejection changed campaign state: " + scenario.Name);
            results.Add(new JObject { ["scenario"] = scenario.Name, ["message"] = text, ["stateUnchanged"] = true });
        }

        using var changedModule = ModuleDefinition.ReadModule(method.Module.FullyQualifiedName);
        var changed = (MethodDefinition)changedModule.LookupToken(method.MetadataToken);
        changed.Body.Instructions.Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && Equals(instruction.Operand, " at ")).Operand = " near ";
        var beforeInstructions = changed.Body.Instructions.Select(instruction => (instruction.OpCode, instruction.Operand)).ToArray();
        var warnings = new List<string>();
        using var context = new ILContext(changed);
        context.Invoke(il => AircraftMessageTranslation.TranslateIL(il, method, catalog, warnings.Add));
        if (warnings.Count != 1 || !beforeInstructions.SequenceEqual(changed.Body.Instructions.Select(instruction => (instruction.OpCode, instruction.Operand))))
            errors.Add("Unrecognized aircraft message generation was changed or skipped without a warning.");
        return new JObject { ["rejectionScenarios"] = results, ["unrecognizedMessageLoggedAndPreserved"] = warnings.Count == 1 };
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, All)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, All)!.SetValue(target, value);

    public class CatalogProxy : DispatchProxy
    {
        public object Aircraft = null!;
        public string AircraftName = null!;
        public int NameRequests;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == "Aircraft") return Aircraft;
            if (method?.Name == "DisplayName") { NameRequests++; return AircraftName; }
            throw new InvalidOperationException("Unexpected unit-catalog request: " + method);
        }
    }

    private static IList Metadata(CodeInstruction instruction, string name) => (IList)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!;

    private static DynamicMethod Compile(MethodInfo method, IEnumerable<CodeInstruction> instructions)
    {
        var result = new DynamicMethod("TranslatedAircraftAlert", method.ReturnType,
            new[] { method.DeclaringType! }.Concat(method.GetParameters().Select(parameter => parameter.ParameterType)).ToArray(), method.DeclaringType!, true);
        var generator = result.GetILGenerator();
        foreach (var local in method.GetMethodBody()!.LocalVariables) generator.DeclareLocal(local.LocalType);
        var code = instructions.ToArray();
        var labels = code.SelectMany(instruction => Metadata(instruction, "labels").Cast<Label>()).Distinct().ToDictionary(label => label, _ => generator.DefineLabel());
        foreach (var instruction in code)
        {
            if (Metadata(instruction, "blocks").Count != 0) throw new InvalidOperationException("The aircraft fixture requires an exception-handler emitter.");
            foreach (var label in Metadata(instruction, "labels").Cast<Label>()) generator.MarkLabel(labels[label]);
            switch (instruction.operand)
            {
                case null: generator.Emit(instruction.opcode); break;
                case string text: generator.Emit(instruction.opcode, text); break;
                case Type type: generator.Emit(instruction.opcode, type); break;
                case FieldInfo field: generator.Emit(instruction.opcode, field); break;
                case MethodInfo call: generator.Emit(instruction.opcode, call); break;
                case ConstructorInfo constructor: generator.Emit(instruction.opcode, constructor); break;
                case Label label: generator.Emit(instruction.opcode, labels[label]); break;
                case int index when instruction.opcode.OperandType == OperandType.ShortInlineVar: generator.Emit(instruction.opcode, checked((byte)index)); break;
                case int value: generator.Emit(instruction.opcode, value); break;
                default: throw new InvalidOperationException("Unexpected aircraft instruction: " + instruction);
            }
        }
        return result;
    }
}
