using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DynamicCampaignJapanese;
using Newtonsoft.Json.Linq;

internal static class CampaignShipNameChecks
{
    public static JObject Verify(string root, Assembly original, List<string> errors)
    {
        var english = new Dictionary<string, string>
        {
            ["Default"] = "Fixture class,Fixture",
            ["Variant1"] = "Shared Name X-01",
            ["Variant2"] = "Standard Name X-02"
        };
        var japanese = new Dictionary<string, string>
        {
            ["Default"] = "テスト艦級,Fixture",
            ["Variant1"] = "標準の第一艦 X-01",
            ["Variant2"] = "標準の第二艦 X-02"
        };
        var gameNames = ShipNameTranslations.Create(new[] { "fixture_unit" }, _ => english.Keys,
            (_, key) => english.GetValueOrDefault(key), (_, key) => japanese.GetValueOrDefault(key));
        var path = Path.Combine(root, "artifacts", "campaign-ship-names-fixture.json");
        var data = new JObject
        {
            ["format"] = 1,
            ["ships"] = new JObject
            {
                ["fixture_unit"] = new JObject
                {
                    ["Shared Name X-01"] = "辞書優先の第一艦 X-01",
                    ["Campaign Only X-03"] = "独自の第三艦 X-03"
                },
                ["another_fixture_unit"] = new JObject { ["Shared Name X-01"] = "別ユニットの第一艦 X-01" }
            }
        };
        File.WriteAllText(path, data.ToString());
        var campaignNames = CampaignShipNames.Load(path);
        File.Delete(path);
        var integration = new ShipDisplayIntegration(original, gameNames, campaignNames, () => false);
        var hullType = original.GetType("DynamicCampaignMod.Core.State.Hull", true)!;
        var fields = new[] { "UnitIni", "Name", "Variant" }.Select(name => hullType.GetField(name)!).ToArray();
        var results = new JArray();
        void Check(string label, string? unit, string? name, string? expected, string variant = "Variant1")
        {
            var hull = Activator.CreateInstance(hullType)!;
            fields[0].SetValue(hull, unit);
            fields[1].SetValue(hull, name);
            fields[2].SetValue(hull, variant);
            var before = fields.Select(field => field.GetValue(hull)).ToArray();
            var display = integration.NameOf(hull);
            if (display != expected) errors.Add("Campaign ship display / " + label + ": expected " + expected + ", got " + display);
            var unchanged = before.SequenceEqual(fields.Select(field => field.GetValue(hull)));
            if (!unchanged) errors.Add("Campaign ship display modified hull identity: " + label);
            results.Add(new JObject { ["scenario"] = label, ["display"] = display, ["identityUnchanged"] = unchanged });
        }
        Check("dictionary overrides a matching game name", "fixture_unit", "Shared Name X-01", "辞書優先の第一艦 X-01");
        Check("dictionary is independent of the selected variant", "fixture_unit", "Shared Name X-01", "辞書優先の第一艦 X-01", "Variant2");
        Check("game-name fallback", "fixture_unit", "Standard Name X-02", "標準の第二艦 X-02", "Variant2");
        Check("name absent from game data", "fixture_unit", "Campaign Only X-03", "独自の第三艦 X-03");
        Check("same name scoped to another unit", "another_fixture_unit", "Shared Name X-01", "別ユニットの第一艦 X-01");
        Check("unregistered unit", "unregistered_unit", "Shared Name X-01", "Shared Name X-01");
        Check("user name with matching substring", "fixture_unit", "My Campaign Only X-03", "My Campaign Only X-03");
        Check("user name with extra suffix", "fixture_unit", "Campaign Only X-03 II", "Campaign Only X-03 II");
        Check("already translated name", "fixture_unit", "独自の第三艦 X-03", "独自の第三艦 X-03");
        Check("case-insensitive lookup", "FIXTURE_UNIT", "campaign only x-03", "独自の第三艦 X-03");
        Check("missing unit", null, "Campaign Only X-03", "Campaign Only X-03");
        Check("missing name", "fixture_unit", null, null);
        var rejected = 0;
        foreach (var invalid in new[]
        {
            "{\"format\":1,\"ships\":{\"unit\":{\"Name\":\"\"}}}",
            "{\"format\":1,\"ships\":{\"unit\":{\"Name\":\"甲\",\"name\":\"乙\"}}}"
        })
        {
            File.WriteAllText(path, invalid);
            try { CampaignShipNames.Load(path); errors.Add("Invalid or ambiguous campaign ship-name data was accepted."); }
            catch (InvalidDataException) { rejected++; }
            finally { File.Delete(path); }
        }
        return new JObject { ["scenarios"] = results, ["invalidDictionariesRejected"] = rejected };
    }
}
