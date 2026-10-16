using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicCampaignJapanese;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class WarPlanDisplayChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static JObject Verify(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog, string originalDirectory, List<string> errors)
    {
        var active = typeof(OriginalModIntegration).GetField("current", All)!;
        var previous = active.GetValue(null);
        active.SetValue(null, integration);
        try
        {
            var path = Path.Combine(originalDirectory, "dynamic_campaigns", "pacific_1985_red_tide", "campaign.json");
            var source = JObject.Parse(File.ReadAllText(path));
            var translated = JObject.Parse(new CampaignTranslator(catalog).Translate(source.ToString()));
            var admiraltyType = original.GetType("DynamicCampaignMod.Core.Sim.Admiralty", true)!;
            var stateType = original.GetType("DynamicCampaignMod.Core.State.CampaignState", true)!;
            var entryType = original.GetType("DynamicCampaignMod.Core.Data.WarPlanEntry", true)!;
            var baseType = original.GetType("DynamicCampaignMod.Core.Data.BaseDefinition", true)!;
            var method = admiraltyType.GetMethod("WarPlanOrder", All)!;
            var compiled = TranslationILChecks.Compile(method, integration);
            var admiralty = RuntimeHelpers.GetUninitializedObject(admiraltyType);
            admiraltyType.GetField("_state", All)!.SetValue(admiralty, Activator.CreateInstance(stateType));
            var cases = 0;
            foreach (var row in (JArray)source["warPlan"]!)
            {
                var baseRow = ((JArray)translated["bases"]!).Single(b => (string?)b["id"] == (string?)row["target"]);
                var target = JsonConvert.DeserializeObject(baseRow.ToString(), baseType)!;
                foreach (var name in new[] { (string)row["name"]!, " Unregistered region ", " " })
                {
                    var data = (JObject)row.DeepClone();
                    data["name"] = name;
                    var entry = JsonConvert.DeserializeObject(data.ToString(), entryType)!;
                    var before = JsonConvert.SerializeObject(entry);
                    var canonicalKey = (string)entryType.GetProperty("Key")!.GetValue(entry)!;
                    var expected = JObject.FromObject(method.Invoke(admiralty, new[] { entry, (object)cases, target })!);
                    var output = JObject.FromObject(compiled.Invoke(null, new[] { admiralty, entry, (object)cases, target })!);
                    var title = (string)output["Title"]!;
                    var region = string.IsNullOrWhiteSpace(name) ? (string)baseRow["name"]! : catalog.DisplayText("warPlanRegion", name.Trim());
                    if (!title.Contains(region, StringComparison.Ordinal) || title == (string?)expected["Title"])
                        errors.Add("A war-plan title was untranslated or lost its region: " + name);
                    if ((string?)output["WarPlanKey"] != canonicalKey || JsonConvert.SerializeObject(entry) != before)
                        errors.Add("A war-plan display translation changed its saved key or source entry: " + name);
                    if ((string?)output["Briefing"] == (string?)expected["Briefing"])
                        errors.Add("A war-plan briefing was left untranslated: " + name);
                    output["Title"] = expected["Title"];
                    output["Briefing"] = expected["Briefing"];
                    if (!JToken.DeepEquals(expected, output)) errors.Add("A translated war-plan order changed a non-display field: " + name);
                    cases++;
                }
            }
            return new JObject { ["orderCases"] = cases, ["canonicalKeysAndSourceEntriesPreserved"] = true,
                ["unknownRegionsAndBlankNameFallbackPreserved"] = true };
        }
        finally { active.SetValue(null, previous); }
    }
}
