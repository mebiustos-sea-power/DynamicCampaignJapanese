using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DynamicCampaignJapanese;

public sealed class CampaignTranslator
{
    private readonly TranslationCatalog catalog;

    public CampaignTranslator(TranslationCatalog catalog) => this.catalog = catalog;

    public string Translate(string json)
    {
        var root = JObject.Parse(json);
        TranslateFields(root, "", "name", "description");
        TranslateRows(root["sides"], "sides", "name", "hq", "command");
        TranslateRows(root["bases"], "bases", "name");
        TranslateRows(root["labels"], "labels", "text");

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["forces"] is JArray forces)
        foreach (var force in forces)
        {
            var source = (string?)force["name"];
            TranslateFields(force, "forces", "name");
            if (source != null && (string?)force["name"] != source) renamed.Add(source, (string)force["name"]!);
        }
        if (root["story"]?["opening"] is JObject opening)
        foreach (var side in opening.Properties())
        foreach (var paragraphGroup in ((JObject)side.Value).Properties())
        {
            if (paragraphGroup.Value is not JArray paragraphs) continue;
            for (var index = 0; index < paragraphs.Count; index++)
                if (paragraphs[index].Type == JTokenType.String)
                    paragraphs[index] = catalog.CampaignText("story.opening", (string)paragraphs[index]!);
        }
        if (root["ranks"] is JObject ranks)
        foreach (var side in ranks.Properties()) TranslateRows(side.Value, "ranks", "name");

        TranslateReferences(root["patrols"], renamed, "force");
        TranslateReferences(root["invasions"], renamed, "landing", "escort", "troops");
        return root.ToString(Formatting.None);
    }

    private void TranslateRows(JToken? rows, string group, params string[] fields)
    {
        if (rows is not JArray array) return;
        foreach (var row in array) TranslateFields(row, group, fields);
    }

    private void TranslateFields(JToken row, string group, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (row[field]?.Type != JTokenType.String) continue;
            var key = group.Length == 0 ? field : group + "." + field;
            row[field] = catalog.CampaignText(key, (string)row[field]!);
        }
    }

    private static void TranslateReferences(JToken? rows, IDictionary<string, string> renamed, params string[] fields)
    {
        if (rows is not JArray array) return;
        foreach (var row in array)
        foreach (var field in fields)
        {
            var source = row[field]?.Type == JTokenType.String ? (string?)row[field] : null;
            if (source != null && renamed.TryGetValue(source, out var translated)) row[field] = translated;
        }
    }
}
