using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicCampaignJapanese;

public sealed class TranslationCatalog
{
    public IReadOnlyDictionary<string, string> Strings { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Methods { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Campaign { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Display { get; }
    public IReadOnlyDictionary<string, CampaignTranslationCatalog> Campaigns { get; }

    private TranslationCatalog(JObject data, string directory)
    {
        if ((int?)data["format"] != 1) throw new InvalidDataException("Unsupported translation-data format.");
        Strings = Dictionary(Required(data, "strings"));
        Methods = Groups(Required(data, "methods"));
        Campaign = Groups(Required(data, "campaign"));
        var campaigns = new Dictionary<string, CampaignTranslationCatalog>(StringComparer.Ordinal);
        if (data["campaignFiles"] != null)
            foreach (var file in Dictionary(Required(data, "campaignFiles")))
                campaigns.Add(file.Key, new CampaignTranslationCatalog(file.Key, Read(Path.Combine(directory, file.Value))));
        Campaigns = campaigns;
        Display = data["display"] == null ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal) : Groups(Required(data, "display"));
    }

    public static TranslationCatalog Load(string path) => new(Read(path), Path.GetDirectoryName(Path.GetFullPath(path))!);

    private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
    {
        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
    });

    public string CampaignText(string group, string source, string? campaignId = null)
    {
        if (campaignId != null && Campaigns.TryGetValue(campaignId, out var campaign) &&
            campaign.Groups.TryGetValue(group, out var scoped) && scoped.TryGetValue(source, out var specific)) return specific;
        return Campaign.TryGetValue(group, out var dictionary) && dictionary.TryGetValue(source, out var translated)
            ? translated : source;
    }

    public string DisplayText(string group, string source) =>
        Display.TryGetValue(group, out var dictionary) && dictionary.TryGetValue(source, out var translated) ? translated : source;

    internal static JObject Required(JObject data, string key)
    {
        return data[key] as JObject ?? throw new InvalidDataException("Translation group is missing: " + key);
    }

    private static IReadOnlyDictionary<string, string> Dictionary(JObject data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.Properties())
        {
            if (property.Value.Type != JTokenType.String)
                throw new InvalidDataException("A translation is not a string: " + property.Name);
            result.Add(property.Name, (string)property.Value!);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Groups(JObject data)
    {
        return data.Properties().ToDictionary(property => property.Name,
            property => Dictionary(property.Value as JObject ?? throw new InvalidDataException("A translation group is not an object: " + property.Name)),
            StringComparer.Ordinal);
    }
}
