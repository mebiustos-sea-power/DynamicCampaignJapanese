using System;
using System.Collections.Generic;
using System.Reflection;

namespace DynamicCampaignJapanese;

public sealed class GameLoadoutNames
{
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public GameLoadoutNames(Type localizationLoader, Type iniHandler)
    {
        var open = localizationLoader.GetMethod("OpenVirtualLocalizationFile", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) })
            ?? throw new MissingMethodException(localizationLoader.FullName, "OpenVirtualLocalizationFile");
        var keys = iniHandler.GetMethod("GetAllKeysOfSection", new[] { typeof(string) })
            ?? throw new MissingMethodException(iniHandler.FullName, "GetAllKeysOfSection");
        var read = iniHandler.GetMethod("readValue", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) })
            ?? throw new MissingMethodException(iniHandler.FullName, "readValue");
        var japanese = open.Invoke(null, new object[] { "language_ja", "loadout_names", "loadout_names", true })
            ?? throw new InvalidOperationException("The game's Japanese loadout-name data is unavailable.");
        foreach (var key in (IEnumerable<string>)keys.Invoke(japanese, new object[] { "LoadoutNames" })!)
        {
            var name = (string?)read.Invoke(japanese, new object[] { "LoadoutNames", key, "", false });
            if (!string.IsNullOrWhiteSpace(name)) names[key] = name!;
        }
        if (names.Count == 0) throw new InvalidOperationException("No Japanese loadout-name data was loaded.");
    }

    public string DisplayName(string key) => names.TryGetValue(key, out var name) ? name : key;

    public static GameLoadoutNames Load(Assembly game) => new(
        game.GetType("SeaPower.LocalizationLoader", true)!, game.GetType("SeaPower.IniHandler", true)!);
}
