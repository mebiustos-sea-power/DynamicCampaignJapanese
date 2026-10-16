using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DynamicCampaignJapanese;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class IdentifierBranchChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static JObject Verify(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog,
        string campaign, List<string> errors)
    {
        var definitionType = original.GetType("DynamicCampaignMod.Core.Data.CampaignDefinition", true)!;
        var defenders = original.GetType("DynamicCampaignMod.Core.Sim.Invasions", true)!.GetMethod("MostDefenders", All)!;
        var translatedDefenders = Compile(defenders, integration);
        var garrisonCases = new JArray();
        foreach (var mode in new[] { "side-specific depot", "generic depot", "base-kind fallback" })
        {
            var data = JObject.Parse(campaign);
            var depot = ((JArray)data["bases"]!).First(row => (bool?)row["depot"] == true);
            var sizes = (JObject)data["ground"]!["garrisonSize"]!;
            sizes.RemoveAll();
            sizes[(string)depot["kind"]!] = new JObject { ["airDefence"] = 1, ["troops"] = 2 };
            if (mode != "base-kind fallback") sizes["Depot"] = new JObject { ["airDefence"] = 3, ["troops"] = 4 };
            if (mode == "side-specific depot") sizes["Depot:" + (string)depot["side"]!] = new JObject { ["airDefence"] = 5, ["troops"] = 6 };
            var definition = JsonConvert.DeserializeObject(data.ToString(), definitionType)!;
            var row = ((System.Collections.IEnumerable)definitionType.GetField("bases")!.GetValue(definition)!).Cast<object>()
                .Single(value => (string)value.GetType().GetField("id")!.GetValue(value)! == (string)depot["id"]!);
            var args = new[] { definition, row };
            var expected = (int)defenders.Invoke(null, args)!;
            var actual = (int)translatedDefenders.Invoke(null, args)!;
            if (actual != expected) errors.Add("Localization changed a depot's defenders: " + mode + " / " + expected + " -> " + actual);
            garrisonCases.Add(new JObject { ["branch"] = mode, ["original"] = expected, ["localized"] = actual });
        }
        var sensorType = original.GetType("DynamicCampaignMod.Core.Intel.Detection+Sensor", true)!;
        var sourceFor = sensorType.GetMethod("SourceFor", All)!;
        var translatedSource = Compile(sourceFor, integration);
        var sourceCases = 0;
        var literals = catalog.Methods[MethodIdentity.Of(sourceFor)];
        foreach (var kind in new[] { "aircraft", "submarine", "ship", "airfield", "coast" })
        foreach (var contact in Enum.GetValues(sourceFor.GetParameters()[0].ParameterType))
        {
            var sensor = Activator.CreateInstance(sensorType, true)!;
            sensorType.GetField("What", All)!.SetValue(sensor, kind);
            sensorType.GetField("Name", All)!.SetValue(sensor, "IdentifierFixture");
            var before = (string)sourceFor.Invoke(sensor, new[] { contact })!;
            var after = (string)translatedSource.Invoke(null, new[] { sensor, contact })!;
            var prefix = before[..before.IndexOf("IdentifierFixture", StringComparison.Ordinal)];
            var expected = (literals.TryGetValue(prefix, out var display) ? display : prefix) + "IdentifierFixture";
            if (after != expected) errors.Add("Localization changed the detected contact's source classification: " + kind + " / " + contact);
            sourceCases++;
        }
        var sourceOf = original.GetType("DynamicCampaignMod.Core.Sim.OrderBriefing", true)!.GetMethod("SourceOf", All)!;
        var translatedSourceOf = Compile(sourceOf, integration);
        var ownSensors = (string)translatedSourceOf.Invoke(null, new object[] { "our sensors" })!;
        var unknown = (string)translatedSourceOf.Invoke(null, new object[] { "unknown-source-fixture" })!;
        if (ownSensors == "our sensors" || ownSensors == unknown || unknown != "unknown-source-fixture")
            errors.Add("An own-sensor report lost its known source or an unknown source was rewritten.");
        return new JObject { ["garrisonCases"] = garrisonCases, ["sensorClassificationCases"] = sourceCases,
            ["ownSensorSourceRecognized"] = ownSensors != "our sensors", ["unknownSourcePreserved"] = unknown == "unknown-source-fixture" };
    }

    private static MethodInfo Compile(MethodInfo method, OriginalModIntegration integration) =>
        TranslationILChecks.Compile(method, integration);
}
