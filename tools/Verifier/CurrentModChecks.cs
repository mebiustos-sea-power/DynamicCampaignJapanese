using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DynamicCampaignJapanese;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class CurrentModChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static int Run(string root, string originalDirectory, string baselineDirectory, Assembly original,
        OriginalModIntegration integration, TranslationCatalog catalog, ShipNameTranslations shipNames,
        CampaignShipNames campaignNames, JObject report, List<string> errors)
    {
        var warnings = new List<string>();
        var plan = integration.Plan(original, warnings.Add);
        foreach (var warning in warnings) errors.Add(warning);
        using var module = ModuleDefinition.ReadModule(original.Location);
        var checkedMethods = 0;
        var changedLiterals = 0;
        foreach (var method in plan)
        {
            var identity = MethodIdentity.Of(method);
            var definition = (MethodDefinition)module.LookupToken(method.MetadataToken);
            var before = Shape(definition);
            var literals = definition.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).ToDictionary(i => i, i => (string)i.Operand);
            using (var context = new ILContext(definition)) context.Invoke(il => integration.TranslateIL(il, method));
            foreach (var pair in literals)
            {
                if (!Equals(pair.Key.Operand, pair.Value)) changedLiterals++;
                var next = pair.Key.Next;
                if (next?.Operand is MethodReference call && call.DeclaringType.FullName == "DynamicCampaignMod.UI.Kit.Observable" &&
                    call.Name is "Set" or "OnChanged" && !Equals(pair.Key.Operand, pair.Value))
                    errors.Add("A binding notification was translated: " + identity + " / " + pair.Value);
            }
            if (identity != OriginalModIntegration.XamlLoad && identity != OriginalModIntegration.XamlParse &&
                identity != OriginalModIntegration.CampaignLoad && identity != AircraftMessageTranslation.Target)
            {
                // The quantity formatter changes a leaf call; its numeric behavior is checked separately.
                if (identity.Contains("::<AirGroupText>g__Count|", StringComparison.Ordinal)) continue;
                if (before != Shape(definition)) errors.Add("Localization changed control flow or a non-text operand: " + identity);
                checkedMethods++;
            }
        }
        report["ilChecks"] = new JObject { ["plannedMethods"] = plan.Count, ["controlFlowPreservedIn"] = checkedMethods,
            ["changedDisplayLiterals"] = changedLiterals, ["warnings"] = JArray.FromObject(warnings) };
        report["translationCoverage"] = TranslationCoverageChecks.Verify(root, originalDirectory, original, catalog, errors);
        report["xamlChecks"] = VerifyMarkup(module, catalog, errors);
        var campaignPath = Path.Combine(originalDirectory, "dynamic_campaigns", "east_asia_1985", "campaign.json");
        var source = File.ReadAllText(campaignPath);
        var translated = new CampaignTranslator(catalog).Translate(source);
        report["campaignChecks"] = VerifyCampaign(source, translated, errors);
        var additionalCampaigns = new JObject();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(originalDirectory, "dynamic_campaigns"), "campaign.json", SearchOption.AllDirectories))
        {
            var input = File.ReadAllText(file);
            var id = (string)JObject.Parse(input)["id"]!;
            if (id == "bungalow.east_asia_1985" || !catalog.Campaigns.ContainsKey(id)) continue;
            additionalCampaigns[id] = VerifyCampaign(input, new CampaignTranslator(catalog).Translate(input), errors);
        }
        report["additionalCampaignChecks"] = additionalCampaigns;
        report["campaignFileChecks"] = CampaignFileChecks.Verify(errors);
        report["warPlanDisplayChecks"] = WarPlanDisplayChecks.Verify(original, integration, catalog, originalDirectory, errors);
        report["identifierBranchChecks"] = IdentifierBranchChecks.Verify(original, integration, catalog, source, errors);
        report["messageBehaviorChecks"] = MessageBehaviorChecks.Verify(original, integration, errors);
        report["uiStateBehaviorChecks"] = UiStateBehaviorChecks.Verify(original, integration, catalog, errors);
        report["saves"] = VerifySaves(original, baselineDirectory, translated, shipNames, campaignNames, errors);
        report["contextualMessageChecks"] = ContextualMessageChecks.VerifyAircraft(original, integration, catalog, errors);
        report["quantityDisplayChecks"] = QuantityDisplayChecks.Verify(original, integration, catalog, errors);
        report["compatibilityChecks"] = CompatibilityChecks.VerifyPartialTranslation(root, errors);
        report["originalVersion"] = original.GetName().Version!.ToString();
        report["gameRenderingTested"] = false;
        report["passed"] = errors.Count == 0;
        report["errors"] = JArray.FromObject(errors);
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "artifacts", "current-verification-report.json"), report.ToString(Formatting.Indented));
        Console.WriteLine(report.ToString(Formatting.None));
        return errors.Count == 0 ? 0 : 1;
    }

    private static string Shape(MethodDefinition method)
    {
        var instructions = method.Body.Instructions.ToList();
        if (instructions.Count >= 3 && instructions[1].Operand is MethodReference entry &&
            (entry.DeclaringType.FullName == typeof(BriefingTextCompatibility).FullName || entry.DeclaringType.FullName == typeof(InternalNameCompatibility).FullName) && instructions[2].OpCode == OpCodes.Starg_S)
            instructions.RemoveRange(0, 3);
        instructions.RemoveAll(i => i.Operand is MethodReference call &&
            (call.DeclaringType.FullName == typeof(InternalNameCompatibility).FullName ||
             call.DeclaringType.FullName == typeof(BriefingTextCompatibility).FullName ||
             (call.DeclaringType.FullName == typeof(OriginalModIntegration).FullName && call.Name is nameof(OriginalModIntegration.BattleOutcome) or nameof(OriginalModIntegration.WarPlanRegion))));
        string Operand(object? value) => value switch
        {
            Instruction instruction => "@" + instructions.IndexOf(instruction),
            ILLabel label => "@" + instructions.IndexOf(label.Target),
            Instruction[] targets => string.Join(",", targets.Select(target => "@" + instructions.IndexOf(target))),
            ILLabel[] targets => string.Join(",", targets.Select(target => "@" + instructions.IndexOf(target.Target))),
            _ => value?.ToString() ?? ""
        };
        // ILContext may widen a short branch; compare its destination, not its byte encoding.
        return string.Join("\n", instructions.Select(i =>
            (i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Name[..^2] : i.OpCode.Name) + ":" +
            (i.OpCode == OpCodes.Ldstr ? "text" : Operand(i.Operand)))) +
            "\nEH:" + string.Join(";", method.Body.ExceptionHandlers.Select(h =>
                h.HandlerType + ":" + Operand(h.TryStart) + ":" + Operand(h.TryEnd) + ":" + Operand(h.HandlerStart) + ":" +
                Operand(h.HandlerEnd) + ":" + Operand(h.FilterStart) + ":" + h.CatchType));
    }

    private static JObject VerifyMarkup(ModuleDefinition module, TranslationCatalog catalog, List<string> errors)
    {
        var translator = new MarkupTranslator(catalog);
        var changed = 0;
        var count = 0;
        foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(resource.GetResourceStream());
            var source = reader.ReadToEnd();
            var left = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
            var right = XDocument.Parse(translator.Translate(source), LoadOptions.PreserveWhitespace);
            var a = left.Descendants().ToArray();
            var b = right.Descendants().ToArray();
            if (a.Length != b.Length) { errors.Add("XAML structure changed: " + resource.Name); continue; }
            for (var index = 0; index < a.Length; index++)
            foreach (var attribute in a[index].Attributes())
            {
                var actual = b[index].Attribute(attribute.Name);
                if (actual == null) { errors.Add("XAML attribute removed: " + resource.Name + " / " + attribute.Name); continue; }
                if (actual.Value == attribute.Value) continue;
                if (attribute.Name.NamespaceName.Length != 0 || attribute.Value.StartsWith("{", StringComparison.Ordinal) ||
                    attribute.Name.LocalName is not ("Text" or "Content" or "ToolTip" or "Header" or "Value"))
                    errors.Add("A XAML binding or non-display attribute changed: " + resource.Name + " / " + attribute.Name);
                changed++;
                actual.Value = attribute.Value;
            }
            if (!XNode.DeepEquals(left, right)) errors.Add("XAML structure or text nodes changed: " + resource.Name);
            count++;
        }
        return new JObject { ["resources"] = count, ["translatedAttributes"] = changed, ["bindingsAndStructurePreserved"] = true };
    }

    private static JObject VerifyCampaign(string source, string translated, List<string> errors)
    {
        var left = JObject.Parse(source);
        var right = JObject.Parse(translated);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var forces = (JArray)left["forces"]!;
        for (var index = 0; index < forces.Count; index++)
        {
            var before = (string)forces[index]!["name"]!;
            var after = (string)right["forces"]![index]!["name"]!;
            if (before != after) renamed.Add(before, after);
        }
        var changed = 0;
        foreach (var value in left.Descendants().OfType<JValue>())
        {
            var actual = right.SelectToken(value.Path);
            if (JToken.DeepEquals(value, actual)) continue;
            var display = Regex.IsMatch(value.Path, @"^(name|description|sides\[\d+\]\.(name|hq|command)|bases\[\d+\]\.name|labels\[\d+\]\.text|forces\[\d+\]\.name|ranks\.[^.]+\[\d+\]\.name|story\.opening\.|texts\.)");
            var reference = Regex.IsMatch(value.Path, @"^(patrols\[\d+\]\.force|invasions\[\d+\]\.(landing|escort|troops))$") &&
                value.Type == JTokenType.String && renamed.TryGetValue((string)value!, out var expected) && (string?)actual == expected;
            if (!display && !reference) errors.Add("A campaign identifier or game parameter changed: " + value.Path);
            if (value.Type != JTokenType.String || actual?.Type != JTokenType.String) errors.Add("A campaign field type changed: " + value.Path);
            changed++;
            if (actual is JValue actualValue) actualValue.Value = value.Value;
        }
        if (!JToken.DeepEquals(left, right)) errors.Add("The translated campaign's structure changed.");
        if ((string?)left["id"] != (string?)right["id"]) errors.Add("Campaign ID changed.");
        return new JObject { ["changedDisplayFieldsAndReferences"] = changed, ["renamedForces"] = renamed.Count,
            ["internalIdentifiersAndGameParametersPreserved"] = true };
    }

    private static JArray VerifySaves(Assembly original, string baselineDirectory, string campaign,
        ShipNameTranslations names, CampaignShipNames campaignNames, List<string> errors)
    {
        var definitionType = original.GetType("DynamicCampaignMod.Core.Data.CampaignDefinition", true)!;
        var definition = JsonConvert.DeserializeObject(campaign, definitionType)!;
        var read = original.GetType("DynamicCampaignMod.Core.State.CampaignSave", true)!.GetMethod("Read", All)!;
        var results = new JArray();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(baselineDirectory, "saves"), "*.json", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta.json", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            var args = new object?[] { text, definition, null, null };
            var state = read.Invoke(null, args);
            if (state == null || args[3] != null) errors.Add("Existing save failed to load: " + Path.GetFileName(file) + " / " + args[3]);
            var display = state == null ? null : ShipNameChecks.VerifySave(original, state, args[2], names, campaignNames, errors);
            if (File.ReadAllText(file) != text) errors.Add("Save verification wrote the input file.");
            results.Add(new JObject { ["name"] = Path.GetFileName(file), ["loaded"] = state != null,
                ["error"] = args[3]?.ToString(), ["shipDisplayChecks"] = display });
        }
        if (results.Count == 0) errors.Add("No existing save was checked.");
        return results;
    }
}
