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

    private TranslationCatalog(JObject data)
    {
        if ((int?)data["format"] != 1) throw new InvalidDataException("Unsupported translation-data format.");
        Strings = Dictionary(Required(data, "strings"));
        Methods = Groups(Required(data, "methods"));
        Campaign = Groups(Required(data, "campaign"));
    }

    public static TranslationCatalog Load(string path)
    {
        var data = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
        });
        return new TranslationCatalog(data);
    }

    public string CampaignText(string group, string source)
    {
        return Campaign.TryGetValue(group, out var dictionary) && dictionary.TryGetValue(source, out var translated)
            ? translated : source;
    }

    private static JObject Required(JObject data, string key)
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

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Groups(JObject data)
    {
        return data.Properties().ToDictionary(property => property.Name,
            property => Dictionary(property.Value as JObject ?? throw new InvalidDataException("A translation group is not an object: " + property.Name)),
            StringComparer.Ordinal);
    }
}
