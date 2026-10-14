using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DynamicCampaignJapanese;

public static class CachedDisplayTranslations
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Apply(Assembly assembly, TranslationCatalog catalog)
    {
        var changed = 0;
        foreach (var entry in catalog.Methods.Where(pair => pair.Key.EndsWith("::.cctor()", StringComparison.Ordinal)))
        {
            var type = assembly.GetType(entry.Key.Substring(0, entry.Key.Length - "::.cctor()".Length), true)!;
            foreach (var field in type.GetFields(Static | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(null);
                if (value is string[] strings)
                {
                    for (var index = 0; index < strings.Length; index++)
                        if (entry.Value.TryGetValue(strings[index], out var translated) && strings[index] != translated)
                        {
                            strings[index] = translated;
                            changed++;
                        }
                }
                else if (value is Array array)
                {
                    for (var index = 0; index < array.Length; index++)
                    {
                        var item = array.GetValue(index);
                        if (item == null) continue;
                        changed += TranslateObject(item, entry.Value);
                        array.SetValue(item, index);
                    }
                }
                else if (value != null && value.GetType().Assembly == assembly)
                    changed += TranslateObject(value, entry.Value);
            }
        }
        return changed;
    }

    private static int TranslateObject(object value, IReadOnlyDictionary<string, string> translations)
    {
        var changed = 0;
        foreach (var field in value.GetType().GetFields(Instance))
            if (field.FieldType == typeof(string) && field.GetValue(value) is string source &&
                translations.TryGetValue(source, out var translated) && source != translated)
            {
                field.SetValue(value, translated);
                changed++;
            }
        return changed;
    }
}
