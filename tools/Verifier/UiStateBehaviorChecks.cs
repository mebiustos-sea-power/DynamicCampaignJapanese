using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DynamicCampaignJapanese;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class UiStateBehaviorChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int deleteCalls;

    public static JObject Verify(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog, List<string> errors)
    {
        var active = typeof(OriginalModIntegration).GetField("current", All)!;
        var previous = active.GetValue(null);
        active.SetValue(null, integration);
        try { return new JObject { ["deleteConfirmation"] = VerifyDelete(original, integration, catalog, errors),
            ["battleResults"] = VerifyOutcomes(original, integration, errors),
            ["internalNames"] = VerifyNames(original, integration, errors) }; }
        finally { active.SetValue(null, previous); }
    }

    private static JObject VerifyDelete(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog, List<string> errors)
    {
        var rowType = original.GetType("DynamicCampaignMod.UI.Saves.SaveRow", true)!;
        var closureType = original.GetType("DynamicCampaignMod.UI.Saves.SaveLoadViewModel+<>c__DisplayClass34_0", true)!;
        var callback = closureType.GetMethod("<Row>b__1", All)!;
        var compiled = TranslationILChecks.Compile(callback, integration, code =>
        {
            foreach (var instruction in code)
                if (instruction.operand is MethodInfo call && (call.Name == "Delete" || call.Name == "Reload"))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(UiStateBehaviorChecks).GetMethod(call.Name == "Delete" ? nameof(DeleteBoundary) : nameof(ReloadBoundary))!;
                }
            return code;
        });
        var row = RuntimeHelpers.GetUninitializedObject(rowType);
        var closure = RuntimeHelpers.GetUninitializedObject(closureType);
        closureType.GetField("row", All)!.SetValue(closure, row);
        closureType.GetField("<>4__this", All)!.SetValue(closure, RuntimeHelpers.GetUninitializedObject(original.GetType("DynamicCampaignMod.UI.Saves.SaveLoadViewModel", true)!));
        var label = catalog.Methods["DynamicCampaignMod.UI.Saves.SaveRow::.ctor()"]["Delete"];
        rowType.GetProperty("DeleteText")!.SetValue(row, label);
        deleteCalls = 0;
        compiled.Invoke(null, new[] { closure });
        var first = deleteCalls;
        var prompted = (string)rowType.GetProperty("DeleteText")!.GetValue(row)! != label;
        compiled.Invoke(null, new[] { closure });
        if (first != 0 || !prompted || deleteCalls != 1) errors.Add("The localized Delete button skipped confirmation or did not delete after confirmation.");
        return new JObject { ["deleteCallsOnFirstClick"] = first, ["confirmationShown"] = prompted,
            ["deleteCallsAfterSecondClick"] = deleteCalls, ["fileOperationsMocked"] = true };
    }

    public static string DeleteBoundary(object? entry) { deleteCalls++; return "Simulated file boundary; no save was deleted."; }
    public static void ReloadBoundary(object view) { }

    private static JArray VerifyOutcomes(Assembly original, OriginalModIntegration integration, List<string> errors)
    {
        var reportType = original.GetType("DynamicCampaignMod.Core.State.BattleReport", true)!;
        var resultType = original.GetType("DynamicCampaignMod.Core.State.SideResult", true)!;
        var getter = reportType.GetProperty("Outcome")!.GetMethod!;
        var translatedGetter = TranslationILChecks.Compile(getter, integration);
        var ctor = original.GetType("DynamicCampaignMod.UI.Report.BattleReportViewModel", true)!.GetConstructors(All).Single();
        CodeInstruction[] DisplayBlock(CodeInstruction[] code)
        {
            var start = Array.FindIndex(code, i => i.operand is MethodInfo m && m.DeclaringType == reportType && m.Name == getter.Name) - 2;
            var end = Array.FindIndex(code, i => i.operand is FieldInfo f && f.Name == "<OutcomeBrush>k__BackingField");
            if (start < 0 || end < start) throw new InvalidOperationException("The battle outcome UI block could not be inspected.");
            var block = code.Skip(start).Take(end - start + 1).ToList();
            foreach (var instruction in block)
            {
                if (instruction.operand is MethodInfo call && call == getter) { instruction.opcode = OpCodes.Call; instruction.operand = translatedGetter; }
                else if (instruction.operand is MethodInfo color && color.DeclaringType?.FullName == "Noesis.Color" && color.Name == "FromArgb")
                    instruction.operand = typeof(UiStateBehaviorChecks).GetMethod(nameof(Color))!;
                else if (instruction.operand is ConstructorInfo brush && brush.DeclaringType?.FullName == "Noesis.SolidColorBrush")
                { instruction.opcode = OpCodes.Call; instruction.operand = typeof(UiStateBehaviorChecks).GetMethod(nameof(Brush))!; }
                else if (instruction.operand is FieldInfo field && field.DeclaringType == ctor.DeclaringType)
                    instruction.operand = typeof(OutcomeProbe).GetField(field.Name == "<Outcome>k__BackingField" ? nameof(OutcomeProbe.Text) : nameof(OutcomeProbe.Color))!;
            }
            block.Add(new CodeInstruction(OpCodes.Ret));
            return block.ToArray();
        }
        var parameters = new[] { typeof(OutcomeProbe) }.Concat(ctor.GetParameters().Select(p => p.ParameterType)).ToArray();
        var display = TranslationILChecks.Compile(ctor, integration, DisplayBlock, typeof(void), parameters);
        var recordType = original.GetType("DynamicCampaignMod.Core.State.WarRecord", true)!;
        var add = recordType.GetMethod("Add")!;
        var translatedAdd = TranslationILChecks.Compile(add, integration, code =>
        {
            foreach (var i in code) if (Equals(i.operand, getter)) { i.opcode = OpCodes.Call; i.operand = translatedGetter; }
            return code;
        });
        var cases = new JArray();
        foreach (var item in new[] { ("Victory", true, false, "勝利"), ("Defeat", false, true, "敗北"), ("Draw", false, false, "引き分け") })
        {
            var report = Activator.CreateInstance(reportType)!;
            reportType.GetField("PlayerSideId")!.SetValue(report, "Blue");
            var sides = (IDictionary)reportType.GetField("Sides")!.GetValue(report)!;
            foreach (var side in new[] { ("Blue", item.Item2), ("Red", item.Item3) })
            {
                var result = Activator.CreateInstance(resultType)!;
                resultType.GetField("SideId")!.SetValue(result, side.Item1);
                resultType.GetField("Victory")!.SetValue(result, side.Item2);
                sides.Add(side.Item1, result);
            }
            var before = JsonConvert.SerializeObject(report);
            var outcome = (string)translatedGetter.Invoke(null, new[] { report })!;
            var probe = new OutcomeProbe();
            display.Invoke(null, new object?[] { probe, null, report, 0, null });
            var expectedColor = item.Item1 == "Victory" ? "255:143:209:143" : item.Item1 == "Defeat" ? "255:226:96:96" : "255:200:200:200";
            var record = Activator.CreateInstance(recordType)!;
            translatedAdd.Invoke(null, new[] { record, report });
            var won = (int)recordType.GetField("Won")!.GetValue(record)!;
            var lost = (int)recordType.GetField("Lost")!.GetValue(record)!;
            if (outcome != item.Item1 || probe.Text != item.Item4 || probe.Color != expectedColor || won != (item.Item2 ? 1 : 0) || lost != (item.Item3 ? 1 : 0))
                errors.Add("Battle outcome label, color or war-record classification changed: " + item.Item1);
            if (JsonConvert.SerializeObject(report) != before) errors.Add("Displaying a battle result rewrote the report.");
            cases.Add(new JObject { ["outcome"] = outcome, ["display"] = probe.Text, ["color"] = probe.Color, ["wins"] = won, ["losses"] = lost,
                ["savedReportUnchanged"] = JsonConvert.SerializeObject(report) == before });
        }
        return cases;
    }

    private static JObject VerifyNames(Assembly original, OriginalModIntegration integration, List<string> errors)
    {
        var stem = original.GetType("DynamicCampaignMod.Core.Sim.AirOperations", true)!.GetMethod("DetachmentStem", All)!;
        var translated = TranslationILChecks.Compile(stem, integration);
        var detachments = 0;
        foreach (var name in new[] { "Squadron det.", "Squadron det. 1", "Squadron det. 12", "Squadron 分遣隊", "Squadron分遣隊1", "Squadron分遣隊12" })
        {
            try { if ((string?)translated.Invoke(null, new object[] { name }) != "Squadron") errors.Add("A split squadron lost its parent name: " + name); }
            catch (TargetInvocationException error) { errors.Add("A split squadron name threw " + error.InnerException!.GetType().Name + ": " + name); }
            detachments++;
        }
        if ((string?)translated.Invoke(null, new object[] { "第1分遣隊" }) != "第1分遣隊") errors.Add("An unrelated squadron name was changed.");
        var reroute = original.GetType("DynamicCampaignMod.Game.Battle.MissionWriter", true)!.GetMethod("RerouteOf", All)!;
        var translatedReroute = TranslationILChecks.Compile(reroute, integration);
        var routes = 0;
        foreach (var name in new[] { "Group scatters", "Group withdraws, ship 1 on its own way", "Group withdraws from what it found", "Group charges", "Unrelated trigger" })
        {
            if (!Equals(reroute.Invoke(null, new object[] { name }), translatedReroute.Invoke(null, new object[] { name }))) errors.Add("A battle trigger's meaning changed: " + name);
            routes++;
        }
        if (!Equals(reroute.Invoke(null, new object[] { "Group scatters" }), translatedReroute.Invoke(null, new object[] { "Groupが散開" })))
            errors.Add("A battle trigger created by the previous Japanese version was not recognized.");
        return new JObject { ["detachmentCases"] = detachments, ["battleTriggerCases"] = routes + 1 };
    }

    public static string Color(byte a, byte r, byte g, byte b) => $"{a}:{r}:{g}:{b}";
    public static string Brush(string color) => color;
    public sealed class OutcomeProbe { public string Text = ""; public string Color = ""; }
}
