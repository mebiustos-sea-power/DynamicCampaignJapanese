using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DynamicCampaignJapanese;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class TranslationCoverageChecks
{
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal) { "Text", "Content", "ToolTip", "Header", "Value" };

    public static JObject Verify(string root, string originalDirectory, Assembly original, TranslationCatalog catalog, List<string> errors)
    {
        using var module = ModuleDefinition.ReadModule(original.Location);
        var markupSources = new HashSet<string>(StringComparer.Ordinal);
        var missingMarkup = new JArray();
        foreach (var resource in module.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(resource.GetResourceStream());
            foreach (var element in XDocument.Parse(reader.ReadToEnd()).Descendants())
            foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName.Length == 0 && Attributes.Contains(a.Name.LocalName)))
            {
                markupSources.Add(attribute.Value);
                var display = attribute.Name.LocalName != "Value" || Attributes.Contains((string?)element.Attribute("Property") ?? "");
                if (display && English(attribute.Value) && !attribute.Value.StartsWith("{", StringComparison.Ordinal) && !catalog.Strings.ContainsKey(attribute.Value))
                    missingMarkup.Add(new JObject { ["view"] = resource.Name, ["source"] = attribute.Value });
            }
        }
        var methods = original.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
            .Where(m => m.GetMethodBody() != null).ToDictionary(MethodIdentity.Of, StringComparer.Ordinal);
        var unusedMethods = new JArray();
        var review = new JArray();
        var decisionReview = new JArray();
        var producers = catalog.Methods.SelectMany(group => group.Value.Where(pair => pair.Key != pair.Value)
            .Select(pair => (Source: pair.Key, Method: group.Key))).ToLookup(pair => pair.Source, StringComparer.Ordinal);
        bool Decision(MethodReference call) => call.DeclaringType.FullName == "System.String" &&
            call.Name is "op_Equality" or "op_Inequality" or "Equals" or "StartsWith" or "EndsWith" or "Contains" or "IndexOf" or "LastIndexOf" ||
            call.DeclaringType.FullName == "System.Text.RegularExpressions.Regex" && call.Name is "Match" or "IsMatch";
        foreach (var pair in methods)
        {
            var definition = (MethodDefinition)module.LookupToken(pair.Value.MetadataToken);
            var literals = definition.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).ToArray();
            catalog.Methods.TryGetValue(pair.Key, out var dictionary);
            foreach (var literal in literals)
            {
                var source = (string)literal.Operand;
                var following = definition.Body.Instructions.SkipWhile(i => i != literal).Skip(1).Take(8).ToArray();
                if (!string.IsNullOrWhiteSpace(source) && producers.Contains(source) && following.Any(i => i.Operand is MethodReference call && Decision(call)))
                    decisionReview.Add(new JObject { ["method"] = pair.Key, ["source"] = source,
                        ["translatedInThisMethod"] = dictionary?.ContainsKey(source) == true,
                        ["producerRules"] = new JArray(producers[source].Select(p => p.Method)),
                        ["followingInstructions"] = new JArray(following.Select(i => i.ToString())) });
                // Runtime-built XAML also passes through MarkupTranslator.
                foreach (Match match in Regex.Matches(source, "(?:Text|Content|ToolTip|Header|Value)=[\"']([^\"']*)[\"']"))
                    markupSources.Add(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
                if (!English(source) || dictionary?.ContainsKey(source) == true) continue;
                review.Add(new JObject { ["method"] = pair.Key, ["source"] = source,
                    ["followingInstructions"] = new JArray(definition.Body.Instructions.SkipWhile(i => i != literal).Skip(1).Take(8).Select(i => i.ToString())) });
            }
            if (dictionary == null) continue;
            var sources = literals.Select(i => (string)i.Operand).ToHashSet(StringComparer.Ordinal);
            foreach (var source in dictionary.Keys.Where(s => !sources.Contains(s)))
                if (!(pair.Key == AircraftMessageTranslation.Target && source == AircraftMessageTranslation.Template) &&
                    !(pair.Key == AircraftQuantityTranslation.Target && source == AircraftQuantityTranslation.Template))
                    unusedMethods.Add(new JObject { ["method"] = pair.Key, ["source"] = source });
        }
        foreach (var missing in catalog.Methods.Keys.Except(methods.Keys, StringComparer.Ordinal)) unusedMethods.Add(missing);
        var unusedMarkup = catalog.Strings.Keys.Where(s => !markupSources.Contains(s)).ToArray();
        var campaignSources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var campaignMissing = new JArray();
        var translatedTexts = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(originalDirectory, "dynamic_campaigns"), "campaign.json", SearchOption.AllDirectories))
        {
            var data = JObject.Parse(File.ReadAllText(file));
            foreach (var field in CampaignFields(data))
            {
                if (!campaignSources.TryGetValue(field.Group, out var values)) campaignSources.Add(field.Group, values = new HashSet<string>(StringComparer.Ordinal));
                values.Add(field.Source);
            }
            if (new DirectoryInfo(Path.GetDirectoryName(file)!).Name != "east_asia_1985") continue;
            var translated = JObject.Parse(new CampaignTranslator(catalog).Translate(data.ToString()));
            foreach (var field in CampaignFields(data))
            {
                if (!English(field.Source)) continue;
                var output = (string?)translated.SelectToken(field.Path);
                if (output == field.Source) campaignMissing.Add(new JObject { ["path"] = field.Path, ["source"] = field.Source });
                if (field.Group != "texts" || output == field.Source) continue;
                translatedTexts++;
                string Placeholders(string value) => string.Join("|", Regex.Matches(value, @"\{[A-Za-z_][A-Za-z0-9_]*\}").Cast<Match>().Select(m => m.Value).OrderBy(s => s));
                if (Placeholders(field.Source) != Placeholders(output!)) errors.Add("Campaign text placeholders changed: " + field.Path);
            }
        }
        var unusedCampaign = new JArray(catalog.Campaign.SelectMany(group => group.Value.Keys
            .Where(s => !campaignSources.TryGetValue(group.Key, out var values) || !values.Contains(s))
            .Select(s => new JObject { ["group"] = group.Key, ["source"] = s })));
        var outcomeGetter = original.GetType("DynamicCampaignMod.Core.State.BattleReport", true)!.GetProperty("Outcome")!.GetMethod!;
        var outcomeSources = ((MethodDefinition)module.LookupToken(outcomeGetter.MetadataToken)).Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToHashSet(StringComparer.Ordinal);
        var unusedDisplay = new JArray(catalog.Display.SelectMany(group => group.Value.Keys.Where(source => group.Key != "battleOutcome" || !outcomeSources.Contains(source))
            .Select(source => new JObject { ["group"] = group.Key, ["source"] = source })));
        if (unusedDisplay.Count != 0) errors.Add("Unused display translation entries: " + unusedDisplay.Count);
        if (unusedMarkup.Length != 0) errors.Add("Unused XAML translation entries: " + unusedMarkup.Length);
        if (unusedMethods.Count != 0) errors.Add("Unused method translation entries: " + unusedMethods.Count);
        if (unusedCampaign.Count != 0) errors.Add("Unused campaign translation entries: " + unusedCampaign.Count);
        if (missingMarkup.Count != 0) errors.Add("Untranslated XAML display attributes: " + missingMarkup.Count);
        if (campaignMissing.Count != 0) errors.Add("Untranslated existing-campaign display fields: " + campaignMissing.Count);
        File.WriteAllText(Path.Combine(root, "artifacts", "method-literal-review.json"), review.ToString(Formatting.Indented));
        File.WriteAllText(Path.Combine(root, "artifacts", "comparison-literal-review.json"), decisionReview.ToString(Formatting.Indented));
        return new JObject { ["unusedMarkup"] = JArray.FromObject(unusedMarkup), ["unusedMethods"] = unusedMethods,
            ["unusedCampaign"] = unusedCampaign, ["unusedDisplay"] = unusedDisplay, ["untranslatedMarkup"] = missingMarkup, ["untranslatedCampaign"] = campaignMissing,
            ["translatedCampaignTexts"] = translatedTexts,
            ["methodLiteralReviewCandidates"] = review.Count, ["methodLiteralReviewFile"] = "artifacts/method-literal-review.json",
            ["methodReviewRequiresHumanClassification"] = true,
            ["comparisonReviewCandidates"] = decisionReview.Count, ["comparisonReviewFile"] = "artifacts/comparison-literal-review.json",
            ["campaignOverrideBehavior"] = VerifyLookup(original, errors) };
    }

    private static bool English(string value) => Regex.IsMatch(value, "[A-Za-z]{2}");

    private static IEnumerable<(string Group, string Path, string Source)> CampaignFields(JObject data)
    {
        foreach (var value in data.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String))
        {
            var path = value.Path;
            var match = Regex.Match(path, @"^(name|description)$|^(sides|bases|labels|forces)\[\d+\]\.([^.]+)$|^ranks\.[^.]+\[\d+\]\.(name)$|^(story\.opening)\.");
            string? group = null;
            if (match.Success)
                group = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value + "." + match.Groups[3].Value : match.Groups[4].Success ? "ranks.name" : "story.opening";
            if (value.Parent is JProperty property && property.Parent == data["texts"]) group = "texts";
            if (group != null && (group is "name" or "description" or "texts" or "story.opening" or "ranks.name" or "sides.name" or "sides.hq" or "sides.command" or "bases.name" or "labels.text" or "forces.name"))
                yield return (group, path, (string)value!);
        }
    }

    private static JObject VerifyLookup(Assembly original, List<string> errors)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"format":1,"strings":{},"methods":{},"campaign":{"texts":{"The fleet is at {where}.":"艦隊所在：{where}"}}}
                """);
            var translator = new CampaignTranslator(TranslationCatalog.Load(path));
            const string source = """
                {"id":"lookup-fixture","texts":{"notice":"The fleet is at {where}.","unknown":"Unregistered notice.","empty":""},"unit":"fixture-id"}
                """;
            var json = translator.Translate(source);
            var definitionType = original.GetType("DynamicCampaignMod.Core.Data.CampaignDefinition", true)!;
            var definition = JsonConvert.DeserializeObject(json, definitionType)!;
            var lookup = definitionType.GetMethod("Text")!;
            string Read(string key) => (string)lookup.Invoke(definition, new object[] { key, "共通説明", new[] { ("where", "横須賀") } })!;
            var notice = Read("notice") == "艦隊所在：横須賀";
            var fallback = Read("absent") == "共通説明" && Read("empty") == "共通説明";
            var unknown = Read("unknown") == "Unregistered notice.";
            if (!notice) errors.Add("Campaign-specific text hid the Japanese fallback or failed to expand a placeholder.");
            if (!fallback) errors.Add("An absent or empty campaign text no longer uses its fallback.");
            if (!unknown) errors.Add("An unregistered campaign text was changed.");
            var expected = JObject.Parse(source);
            expected["texts"]!["notice"] = "艦隊所在：{where}";
            if (!JToken.DeepEquals(expected, JObject.Parse(json))) errors.Add("Campaign text translation changed a key or an unrelated field.");
            return new JObject { ["overridesFallback"] = notice, ["expandsNamedPlaceholders"] = notice, ["unknownTextAndKeysPreserved"] = unknown && JToken.DeepEquals(expected, JObject.Parse(json)),
                ["absentAndEmptyUseFallback"] = fallback };
        }
        finally { File.Delete(path); }
    }
}
