using System;
using System.Collections.Generic;
using System.IO;
using DynamicCampaignJapanese;
using Newtonsoft.Json.Linq;

internal static class CampaignFileChecks
{
    public static JObject Verify(List<string> errors)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dcm-campaign-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var main = Path.Combine(directory, "translation-data.json");
            File.WriteAllText(main, """
                {"format":1,"strings":{},"methods":{},"campaign":{"texts":{"Same report.":"共通報告","Shared guide.":"共通案内"}},
                 "campaignFiles":{"fixture.first":"first.json","fixture.second":"second.json"}}
                """);
            File.WriteAllText(Path.Combine(directory, "first.json"), """
                {"format":1,"campaign":{"texts":{"Same report.":"第一戦域の報告"}}}
                """);
            File.WriteAllText(Path.Combine(directory, "second.json"), """
                {"format":1,"campaign":{"texts":{"Same report.":"第二戦域の報告"}}}
                """);
            var catalog = TranslationCatalog.Load(main);
            File.Delete(Path.Combine(directory, "first.json"));
            File.Delete(Path.Combine(directory, "second.json"));
            var translator = new CampaignTranslator(catalog);
            var cases = 0;
            foreach (var item in new[] { ("fixture.first", "第一戦域の報告"), ("fixture.second", "第二戦域の報告"), ("fixture.unknown", "共通報告") })
            {
                var source = new JObject { ["id"] = item.Item1, ["texts"] = new JObject {
                    ["report"] = "Same report.", ["guide"] = "Shared guide.", ["unknown"] = "Unregistered text." },
                    ["baseId"] = "unchanged-id", ["points"] = 23 };
                var expected = (JObject)source.DeepClone();
                expected["texts"]!["report"] = item.Item2;
                expected["texts"]!["guide"] = "共通案内";
                var actual = JObject.Parse(translator.Translate(source.ToString()));
                if (!JToken.DeepEquals(expected, actual)) errors.Add("Campaign file selection, override priority, shared fallback or unrelated fields changed: " + item.Item1);
                cases++;
            }
            var missingFileDetected = false;
            try { TranslationCatalog.Load(main); }
            catch (FileNotFoundException) { missingFileDetected = true; }
            if (!missingFileDetected) errors.Add("A declared campaign translation file was missing but initialization accepted it.");
            return new JObject { ["selectionCases"] = cases, ["scopedOverridesAndSharedFallback"] = true,
                ["cachedAfterInitialization"] = true, ["missingDeclaredFileDetected"] = missingFileDetected };
        }
        finally { Directory.Delete(directory, true); }
    }
}
