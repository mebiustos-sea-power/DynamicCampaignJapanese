using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DynamicCampaignJapanese;

public sealed class CampaignTranslationCatalog
{
    public string Id { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Groups { get; }

    internal CampaignTranslationCatalog(string id, JObject data)
    {
        if ((int?)data["format"] != 1) throw new InvalidDataException("Unsupported campaign translation format: " + id);
        Id = id;
        Groups = TranslationCatalog.Groups(TranslationCatalog.Required(data, "campaign"));
    }
}
