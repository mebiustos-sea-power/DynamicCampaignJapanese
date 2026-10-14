using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicCampaignJapanese;

public sealed class ShipNameTranslations
{
    private readonly Dictionary<string, UnitNames> units = new(StringComparer.OrdinalIgnoreCase);
    public int UnitCount => units.Count;

    public static ShipNameTranslations Create(IEnumerable<string> unitIds, Func<string, IEnumerable<string>> keys,
        Func<string, string, string?> english, Func<string, string, string?> japanese, Action<string>? logWarning = null)
    {
        var result = new ShipNameTranslations();
        foreach (var id in unitIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var names = new UnitNames(Pair.Parse(english(id, "Default")), Pair.Parse(japanese(id, "Default")),
                Pair.Parse(english(id, "NatoDefault")), Pair.Parse(japanese(id, "NatoDefault")));
            foreach (var key in keys(id).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!key.StartsWith("Variant", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(key.Substring("Variant".Length), out var number) || number < 1) continue;
                var source = Pair.Parse(english(id, key)).Full;
                var translated = Pair.Parse(japanese(id, key)).Full;
                if (source.Length == 0 || translated.Length == 0) continue;
                names.Variants.Add(key, (source, translated));
                if (names.Ships.TryGetValue(source, out var existing) && existing != translated)
                {
                    names.Ships[source] = null;
                    logWarning?.Invoke("Ambiguous ship-name translation; variant required: " + id + " / " + source);
                }
                else if (!names.Ships.ContainsKey(source)) names.Ships.Add(source, translated);
            }
            if (names.Japanese.Full.Length != 0 || names.Ships.Count != 0) result.units.Add(id, names);
        }
        return result;
    }

    public string? Ship(string? unitId, string? variant, string? original)
    {
        if (unitId == null || original == null || !units.TryGetValue(unitId, out var unit)) return original;
        var source = original.Trim();
        if (variant != null && unit.Variants.TryGetValue(variant, out var entry) &&
            string.Equals(source, entry.Source, StringComparison.OrdinalIgnoreCase)) return entry.Translated;
        return unit.Ships.TryGetValue(source, out var translated) && translated != null ? translated : original;
    }

    public string Class(string unitId, string original, bool natoNames)
    {
        if (!units.TryGetValue(unitId, out var unit) || !unit.MatchesClass(original)) return original;
        if (natoNames && unit.JapaneseNato.Full.Length != 0) return unit.JapaneseNato.Full;
        return unit.Japanese.Full.Length != 0 ? unit.Japanese.Full : original;
    }

    private readonly struct Pair
    {
        public string Full { get; }
        public string Short { get; }
        private Pair(string full, string abbreviated) => (Full, Short) = (full, abbreviated);
        public static Pair Parse(string? value)
        {
            var parts = (value ?? "").Split(new[] { ',' }, 2);
            return new Pair(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : parts[0].Trim());
        }
        public bool Matches(string value) => Full.Length != 0 &&
            (string.Equals(Full, value, StringComparison.OrdinalIgnoreCase) || string.Equals(Short, value, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class UnitNames
    {
        public Pair English { get; }
        public Pair Japanese { get; }
        public Pair EnglishNato { get; }
        public Pair JapaneseNato { get; }
        public Dictionary<string, (string Source, string Translated)> Variants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> Ships { get; } = new(StringComparer.OrdinalIgnoreCase);
        public UnitNames(Pair english, Pair japanese, Pair englishNato, Pair japaneseNato) =>
            (English, Japanese, EnglishNato, JapaneseNato) = (english, japanese, englishNato, japaneseNato);
        public bool MatchesClass(string value) => English.Matches(value) || Japanese.Matches(value) ||
            EnglishNato.Matches(value) || JapaneseNato.Matches(value);
    }
}
