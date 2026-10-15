using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DynamicCampaignJapanese;

public sealed class CampaignShipNames
{
    private readonly Dictionary<string, Dictionary<string, string>> ships = new(StringComparer.OrdinalIgnoreCase);
    public int NameCount { get; private set; }

    private CampaignShipNames(JObject data)
    {
        if ((int?)data["format"] != 1) throw new InvalidDataException("Unsupported campaign ship-name format.");
        if (data["ships"] is not JObject groups) throw new InvalidDataException("Campaign ship-name group is missing: ships");
        foreach (var group in groups.Properties())
        {
            if (string.IsNullOrWhiteSpace(group.Name) || group.Name != group.Name.Trim() || group.Value is not JObject entries)
                throw new InvalidDataException("Invalid campaign ship-name unit: " + group.Name);
            if (ships.ContainsKey(group.Name)) throw new InvalidDataException("Duplicate campaign ship-name unit: " + group.Name);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Properties())
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name != entry.Name.Trim() ||
                    entry.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)entry.Value))
                    throw new InvalidDataException("Invalid campaign ship-name translation: " + group.Name + " / " + entry.Name);
                if (names.ContainsKey(entry.Name)) throw new InvalidDataException("Duplicate campaign ship name: " + group.Name + " / " + entry.Name);
                names.Add(entry.Name, (string)entry.Value!);
                NameCount++;
            }
            ships.Add(group.Name, names);
        }
    }

    public static CampaignShipNames Load(string path) => new(JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
    {
        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
    }));

    public bool TryTranslate(string? unitId, string? original, out string translated)
    {
        if (unitId != null && original != null && ships.TryGetValue(unitId, out var names) && names.TryGetValue(original.Trim(), out var value))
        {
            translated = value;
            return true;
        }
        translated = string.Empty;
        return false;
    }
}
