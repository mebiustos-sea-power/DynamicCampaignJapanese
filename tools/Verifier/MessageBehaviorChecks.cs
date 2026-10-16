using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Input;
using DynamicCampaignJapanese;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class MessageBehaviorChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static JObject Verify(Assembly original, OriginalModIntegration integration, List<string> errors)
    {
        var briefingType = original.GetType("DynamicCampaignMod.Core.Sim.Briefing", true)!;
        var messageType = original.GetType("DynamicCampaignMod.Core.State.SideMessage", true)!;
        var kindType = original.GetType("DynamicCampaignMod.Core.State.MessageKind", true)!;
        var text = briefingType.GetMethod("Text")!;
        var parse = briefingType.GetMethod("Parse")!;
        var opening = original.GetType("DynamicCampaignMod.Core.Sim.Story", true)!.GetMethod("IsOpening", All)!;
        var translatedText = TranslationILChecks.Compile(text, integration);
        var translatedParse = TranslationILChecks.Compile(parse, integration);
        var translatedOpening = TranslationILChecks.Compile(opening, integration);
        var paper = Activator.CreateInstance(briefingType)!;
        var fields = new Dictionary<string, string> { ["From"] = "発信者", ["To"] = "宛先", ["Info"] = "通報先",
            ["Subject"] = "SITREP 001 - 全般状況", ["DateTimeGroup"] = "010000ZJUN85", ["Closing"] = "報告終わり。" };
        foreach (var field in fields) briefingType.GetField(field.Key)!.SetValue(paper, field.Value);
        briefingType.GetMethod("Add")!.Invoke(paper, new object[] { "状況", new List<string> { "第1項。", "第2項。" } });
        briefingType.GetMethod("Add")!.Invoke(paper, new object[] { "任務", new List<string> { "港湾を防衛せよ。" } });
        var before = JsonConvert.SerializeObject(paper);
        var canonical = (string)text.Invoke(paper, null)!;
        var localized = (string)translatedText.Invoke(null, new[] { paper })!;
        var restored = translatedParse.Invoke(null, new object[] { localized });
        var roundTrip = restored != null && JsonConvert.SerializeObject(restored) == before;
        if (!roundTrip) errors.Add("Localized briefing serialization lost headers, sections or closing text on readback.");
        if (localized != canonical) errors.Add("Localization changed the briefing's internal serialization format.");
        const string legacy = "発：発信者\n宛：宛先\n通報先：通報先\n件名：SITREP 001 - 全般状況  010000ZJUN85\n\n1. (S) 状況：\n1.1 第1項。\n1.2 第2項。\n\n2. (S) 任務：\n2.1 港湾を防衛せよ。\n\n報告終わり。";
        var legacyPaper = translatedParse.Invoke(null, new object[] { legacy });
        var legacyParsed = legacyPaper != null && JsonConvert.SerializeObject(legacyPaper) == before;
        if (!legacyParsed) errors.Add("A briefing stored by the previous Japanese version cannot be reopened with all its sections.");
        var rowMethod = original.GetType("DynamicCampaignMod.UI.Strategy.MessagesViewModel", true)!.GetMethod("Row", All)!;
        var originalRow = CompileRow(rowMethod, null, opening);
        var translatedRow = CompileRow(rowMethod, integration, translatedOpening);
        object Message(string value, string kind)
        {
            var result = Activator.CreateInstance(messageType)!;
            messageType.GetField("Kind")!.SetValue(result, Enum.Parse(kindType, kind));
            messageType.GetField("Text")!.SetValue(result, value);
            return result;
        }
        var cases = new JArray();
        foreach (var item in new[] { ("new report", localized, "Admiralty", true), ("existing English report", canonical, "Admiralty", true),
            ("existing Japanese report", legacy, "Admiralty", true), ("ordinary message", "通常の通知。", "Other", false),
            ("wrong category", canonical, "Other", false), ("other report", canonical.Replace("SITREP 001", "SITREP 002"), "Admiralty", false) })
        {
            var message = Message(item.Item2, item.Item3);
            var saved = JsonConvert.SerializeObject(message);
            var row = (RowProbe)translatedRow.Invoke(null, new object?[] { null, message })!;
            var expectedVisible = item.Item4 ? 2 : 0;
            var actualOpening = (bool)translatedOpening.Invoke(null, new[] { message })!;
            if (actualOpening != item.Item4 || row.ReadVisibility != expectedVisible || row.ReadCommand == null ||
                (item.Item4 ? row.Text == item.Item2 || row.Text.Contains('\n') : row.Text != item.Item2))
                errors.Add("Message layout or Read action changed: " + item.Item1);
            if (JsonConvert.SerializeObject(message) != saved) errors.Add("Displaying a message rewrote its saved content: " + item.Item1);
            cases.Add(new JObject { ["case"] = item.Item1, ["openingReportRecognized"] = actualOpening,
                ["readButtonVisible"] = row.ReadVisibility == 2, ["singleLineSummary"] = !row.Text.Contains('\n'), ["savedMessageUnchanged"] = saved == JsonConvert.SerializeObject(message) });
        }
        var baseline = (RowProbe)originalRow.Invoke(null, new object?[] { null, Message(canonical, "Admiralty") })!;
        if (baseline.ReadVisibility != 2 || baseline.Text.Contains('\n')) errors.Add("The original message layout fixture is invalid.");
        if (JsonConvert.SerializeObject(paper) != before) errors.Add("Briefing verification changed the input report.");
        var predicates = original.GetType("DynamicCampaignMod.Game.Battle.MissionWriter+<>c", true)!;
        var sectionMethod = predicates.GetMethod("<Text>b__12_3", All)!;
        var sectionFilter = TranslationILChecks.Compile(sectionMethod, integration);
        foreach (var heading in new[] { "SITUATION", "状況", "MISSION", "任務", "EXECUTION", "実施要領" })
        {
            var tuple = Activator.CreateInstance(sectionMethod.GetParameters()[0].ParameterType, heading, new List<string> { "本文" })!;
            var selected = (bool)sectionFilter.Invoke(null, new object?[] { null, tuple })!;
            if (selected != (heading is "SITUATION" or "状況" or "MISSION" or "任務")) errors.Add("Battle briefing section selection depends on language: " + heading);
        }
        var orderFilter = TranslationILChecks.Compile(predicates.GetMethod("<Text>b__12_5", All)!, integration);
        foreach (var line in new[] { "Admiralty order: Task", "海軍司令部命令: 任務", "海軍司令部命令：任務", "一般の報告文。" })
            if ((bool)orderFilter.Invoke(null, new object?[] { null, line })! != (line == "一般の報告文。")) errors.Add("An Admiralty order line was duplicated in the battle briefing: " + line);
        return new JObject { ["roundTripPreserved"] = roundTrip, ["internalFormatPreserved"] = localized == canonical,
            ["existingJapaneseReportParsed"] = legacyParsed, ["messageRows"] = cases, ["renderingDependenciesStubbed"] = true, ["sectionSelectionCases"] = 6, ["orderLineSelectionCases"] = 4 };
    }

    private static MethodInfo CompileRow(MethodInfo method, OriginalModIntegration? integration, MethodInfo opening) =>
        TranslationILChecks.Compile(method, integration, code =>
        {
            foreach (var instruction in code)
            {
                if (instruction.operand is FieldInfo field && field.DeclaringType?.FullName == "DynamicCampaignMod.UI.Strategy.MessagesViewModel" && field.Name == "Brushes")
                { instruction.opcode = OpCodes.Nop; instruction.operand = null; }
                else if (instruction.operand is FieldInfo rowField && rowField.DeclaringType?.FullName == "DynamicCampaignMod.UI.Strategy.MessageRow")
                    instruction.operand = typeof(RowProbe).GetField(rowField.Name)!;
                else if (instruction.operand is FieldInfo font && font.DeclaringType?.FullName == "Noesis.FontWeights")
                { instruction.opcode = OpCodes.Ldnull; instruction.operand = null; }
                else if (instruction.operand is ConstructorInfo ctor && ctor.DeclaringType?.FullName == "DynamicCampaignMod.UI.Strategy.MessageRow")
                    instruction.operand = typeof(RowProbe).GetConstructor(Type.EmptyTypes)!;
                else if (instruction.operand is MethodInfo call)
                {
                    if (call.Name == "IsOpening") instruction.operand = opening;
                    else if (call.DeclaringType?.FullName == "DynamicCampaignMod.UI.Strategy.MessagesViewModel" && call.Name == "Name")
                        instruction.operand = TranslationILChecks.Compile(call, integration);
                    else if (call.DeclaringType?.FullName == "DynamicCampaignMod.UI.Strategy.MessageRow") instruction.operand = typeof(RowProbe).GetMethod(call.Name)!;
                    else if (call.Name == "get_Item" && call.DeclaringType?.Name == "Dictionary`2")
                    { instruction.opcode = OpCodes.Call; instruction.operand = typeof(MessageBehaviorChecks).GetMethod(nameof(Brush))!; }
                    else if (call.DeclaringType?.FullName == "DynamicCampaignMod.UI.Kit.ViewModel" && call.Name == "Command")
                        instruction.operand = typeof(MessageBehaviorChecks).GetMethod(nameof(Command))!;
                }
            }
            return code;
        }, typeof(RowProbe));

    public static object? Brush(int kind) => null;
    public static ICommand Command(string name, Action action) => new CommandProbe(action);
    private sealed class CommandProbe(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
    public sealed class RowProbe
    {
        public object? Message = null;
        public string Time { get; set; } = "";
        public string Category { get; set; } = "";
        public object? CategoryBrush { get; set; }
        public string Text { get; set; } = "";
        public object? Weight { get; set; }
        public ICommand? ShowCommand { get; set; }
        public int ShowVisibility { get; set; }
        public ICommand? ReadCommand { get; set; }
        public int ReadVisibility { get; set; }
    }
}
