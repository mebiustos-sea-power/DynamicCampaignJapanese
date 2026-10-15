using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AnchorChain;
using BepInEx.Logging;
using DynamicCampaignJapanese;
using Newtonsoft.Json.Linq;

internal static class CompatibilityChecks
{
    public static JObject VerifyStartup(List<string> errors)
    {
        var log = (ManualLogSource)typeof(Plugin).GetField("Log", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var entries = new List<LogEventArgs>();
        EventHandler<LogEventArgs> listener = (_, entry) => entries.Add(entry);
        log.LogEvent += listener;
        var entryReturned = false;
        var callbackReturned = false;
        var entryErrorLogged = false;
        var callbackErrorLogged = false;
        try
        {
            try
            {
                new Plugin().TriggerEntryPoint();
                entryReturned = true;
            }
            catch (Exception error) { errors.Add("Initialization error escaped to Anchor Chain: " + error); }
            entryErrorLogged = entries.Any(entry => (entry.Level & LogLevel.Error) != 0);
            if (!entryErrorLogged) errors.Add("Initialization failure was not logged.");
            entries.Clear();
            try
            {
                typeof(Plugin).GetMethod("ApplyTranslations", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                callbackReturned = true;
            }
            catch (Exception error) { errors.Add("Translation setup error escaped to the original mod: " + error); }
            callbackErrorLogged = entries.Any(entry => (entry.Level & LogLevel.Error) != 0);
            if (!callbackErrorLogged) errors.Add("Translation setup failure was not logged.");
        }
        finally { log.LogEvent -= listener; }
        return new JObject
        {
            ["initializationReturnedWithoutThrowing"] = entryReturned,
            ["initializationErrorLogged"] = entryErrorLogged,
            ["translationCallbackReturnedWithoutThrowing"] = callbackReturned,
            ["translationCallbackErrorLogged"] = callbackErrorLogged
        };
    }

    public static JObject VerifyPartialTranslation(string root, List<string> errors)
    {
        var dependency = typeof(Plugin).GetCustomAttributes<ACDependency>().Single();
        var acceptedVersions = new JArray();
        foreach (var version in new[] { "0.24.2", "0.24.4", "1.0.0" })
        {
            if (!dependency.Contains(new Version(version))) errors.Add("Anchor Chain rejected the original mod's version: " + version);
            else acceptedVersions.Add(version);
        }

        const string display = "DynamicCampaignMod.Verification.Display";
        const string removed = display + "::Removed()";
        const string removedCache = "DynamicCampaignMod.Verification.RemovedDisplay::.cctor()";
        var data = new JObject
        {
            ["format"] = 1, ["originalModVersion"] = "0.24.3.0",
            ["strings"] = new JObject(), ["campaign"] = new JObject(),
            ["methods"] = new JObject
            {
                [display + "::Caption()"] = new JObject { ["Speed"] = "速度" },
                [display + "::.cctor()"] = new JObject { ["Weapons"] = "兵装" },
                [removed] = new JObject { ["Removed"] = "削除済み" },
                [removedCache] = new JObject { ["Missing"] = "欠落" }
            }
        };
        var path = Path.Combine(root, "artifacts", "compatibility-translation-data.json");
        File.WriteAllText(path, data.ToString());
        var catalog = TranslationCatalog.Load(path);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynamicCampaignMod") { Version = new Version(7, 0, 0, 0) }, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("CompatibilityFixture");
        var type = module.DefineType(display, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var caption = type.DefineMethod("Caption", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
        var body = caption.GetILGenerator();
        body.Emit(OpCodes.Ldstr, "Speed");
        body.Emit(OpCodes.Ret);
        var labels = type.DefineField("Labels", typeof(string[]), FieldAttributes.Public | FieldAttributes.Static);
        var initializer = type.DefineTypeInitializer().GetILGenerator();
        initializer.Emit(OpCodes.Ldc_I4_2);
        initializer.Emit(OpCodes.Newarr, typeof(string));
        initializer.Emit(OpCodes.Dup);
        initializer.Emit(OpCodes.Ldc_I4_0);
        initializer.Emit(OpCodes.Ldstr, "Weapons");
        initializer.Emit(OpCodes.Stelem_Ref);
        initializer.Emit(OpCodes.Dup);
        initializer.Emit(OpCodes.Ldc_I4_1);
        initializer.Emit(OpCodes.Ldstr, "Unlisted");
        initializer.Emit(OpCodes.Stelem_Ref);
        initializer.Emit(OpCodes.Stsfld, labels);
        initializer.Emit(OpCodes.Ret);
        var created = type.CreateType()!;
        var integration = new OriginalModIntegration(catalog);
        var warnings = new List<string>();
        var plan = integration.Plan(assembly, warnings.Add);
        if (plan.Count != 2 || !plan.Contains(created.GetMethod("Caption")!)) errors.Add("Missing targets prevented planning the remaining translations.");
        if (!warnings.Any(message => message.Contains(removed, StringComparison.Ordinal)) ||
            !warnings.Any(message => message.Contains(removedCache, StringComparison.Ordinal)))
            errors.Add("Missing translation targets were not identified in the log.");

        var translated = new DynamicMethod("TranslatedCaption", typeof(string), Type.EmptyTypes);
        var output = translated.GetILGenerator();
        foreach (var instruction in TranslationILChecks.Read(created.GetMethod("Caption")!, integration))
        {
            if (instruction.operand is string text) output.Emit(instruction.opcode, text);
            else if (instruction.operand == null) output.Emit(instruction.opcode);
            else throw new InvalidOperationException("Unexpected caption instruction: " + instruction);
        }
        var translatedCaption = ((Func<string>)translated.CreateDelegate(typeof(Func<string>)))();
        if (translatedCaption != "速度") errors.Add("The remaining method did not return its Japanese caption.");
        var cachedChanges = CachedDisplayTranslations.Apply(assembly, catalog, warnings.Add);
        var cachedLabels = (string[])created.GetField("Labels")!.GetValue(null)!;
        if (cachedChanges != 1 || cachedLabels[0] != "兵装" || cachedLabels[1] != "Unlisted")
            errors.Add("A missing cached display type prevented translation or changed an unknown label.");
        return new JObject
        {
            ["dependencyAcceptedVersions"] = acceptedVersions,
            ["differentVersionPlanned"] = assembly.GetName().Version!.ToString(),
            ["remainingMethods"] = plan.Count, ["caption"] = translatedCaption,
            ["cachedLabels"] = JArray.FromObject(cachedLabels), ["warnings"] = JArray.FromObject(warnings)
        };
    }
}
