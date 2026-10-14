using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DynamicCampaignJapanese;

public sealed class GameShipNames
{
    public ShipNameTranslations Translations { get; }
    private readonly PropertyInfo natoNames;
    public bool NatoNames => (bool)natoNames.GetValue(null)!;

    public GameShipNames(Type localizationLoader, Type iniHandler, Type options, Action<string>? logWarning = null)
    {
        var open = localizationLoader.GetMethod("OpenVirtualLocalizationFile", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) })
            ?? throw new MissingMethodException(localizationLoader.FullName, "OpenVirtualLocalizationFile");
        var sections = iniHandler.GetMethod("getNamesOfAllSections", Type.EmptyTypes)
            ?? throw new MissingMethodException(iniHandler.FullName, "getNamesOfAllSections");
        var keys = iniHandler.GetMethod("GetAllKeysOfSection", new[] { typeof(string) })
            ?? throw new MissingMethodException(iniHandler.FullName, "GetAllKeysOfSection");
        var read = iniHandler.GetMethod("readValue", new[] { typeof(string), typeof(string), typeof(string), typeof(bool) })
            ?? throw new MissingMethodException(iniHandler.FullName, "readValue");
        natoNames = options.GetProperty("NatoUnitNamesEnabled", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(options.FullName, "NatoUnitNamesEnabled");
        var english = open.Invoke(null, new object[] { "language_en", "vessel_names", "vessel_names", true })
            ?? throw new InvalidOperationException("The game's English vessel-name data is unavailable.");
        var japanese = open.Invoke(null, new object[] { "language_ja", "vessel_names", "vessel_names", true })
            ?? throw new InvalidOperationException("The game's Japanese vessel-name data is unavailable.");
        IEnumerable<string> Sections(object data) => (IEnumerable<string>)sections.Invoke(data, null)!;
        IEnumerable<string> Keys(object data, string unit) => (IEnumerable<string>)keys.Invoke(data, new object[] { unit })!;
        string? Read(object data, string unit, string key) => (string?)read.Invoke(data, new object[] { unit, key, "", false });
        Translations = ShipNameTranslations.Create(Sections(english).Concat(Sections(japanese)),
            unit => Keys(english, unit).Concat(Keys(japanese, unit)),
            (unit, key) => Read(english, unit, key), (unit, key) => Read(japanese, unit, key), logWarning);
        if (Translations.UnitCount == 0) throw new InvalidOperationException("No Japanese vessel-name data was loaded.");
    }

    public static GameShipNames Load(Assembly game, Action<string>? logWarning = null) => new(
        game.GetType("SeaPower.LocalizationLoader", true)!, game.GetType("SeaPower.IniHandler", true)!,
        game.GetType("SeaPower.OptionsManager", true)!, logWarning);
}
