using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;

namespace DynamicCampaignJapanese;

[ACPlugin(Plugin.ModId, "Dynamic Campaign Japanese", "2.0.0", new[] { "dynamiccampaignmod" }, new string[0])]
[ACDependency("dynamiccampaignmod", null, null)]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = "mebiustos.bungalowdynamiccampaign.ja";

    private static readonly ManualLogSource Log = Logger.CreateLogSource("Dynamic Campaign Japanese");
    private static bool installed;
    private static bool scheduled;
    private static Assembly? originalAssembly;
    private static OriginalModIntegration? integration;
    private static Harmony? harmony;

    public void TriggerEntryPoint()
    {
        if (installed || scheduled) return;
        try
        {
            var package = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
            if (!Enabled(package))
            {
                Log.LogInfo("Japanese localization is disabled in the mod manager.");
                return;
            }
            var original = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.GetName().Name == "DynamicCampaignMod");
            if (original == null)
            {
                Log.LogError("The original Dynamic Campaign assembly was not found.");
                return;
            }
            if (!Enabled(Path.GetDirectoryName(original.Location)!))
            {
                Log.LogInfo("The original Dynamic Campaign mod is disabled.");
                return;
            }
            var catalog = TranslationCatalog.Load(Path.Combine(package, "translation-data.json"));
            integration = new OriginalModIntegration(catalog);
            originalAssembly = original;
            harmony = new Harmony(ModId);
            var deferred = original.GetType("DynamicCampaignMod.DeferredPatcher");
            var ready = deferred?.GetMethod("Patch", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (ready == null)
            {
                Log.LogWarning("The original mod's main-menu initialization hook was not found; Japanese localization was not scheduled.");
                return;
            }
            harmony.Patch(ready, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ApplyTranslations), BindingFlags.NonPublic | BindingFlags.Static)));
            scheduled = true;
            Log.LogInfo("Japanese localization is scheduled after the original mod's main-menu initialization.");
        }
        catch (Exception error)
        {
            Log.LogError("Japanese localization initialization failed: " + error);
        }
    }

    private static void ApplyTranslations()
    {
        if (installed) return;
        try
        {
            var count = integration!.Install(originalAssembly!, harmony!, message => Log.LogWarning(message));
            installed = true;
            Log.LogInfo("Independent Japanese localization 2.0.0 active for " + originalAssembly!.GetName().Version + "; methods=" + count);
            try
            {
                var game = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Seapower-Scripts");
                var shipNames = GameShipNames.Load(game, message => Log.LogWarning(message));
                var displays = new ShipDisplayIntegration(originalAssembly!, shipNames.Translations, () => shipNames.NatoNames);
                var shipCount = displays.Install(originalAssembly!, harmony!, message => Log.LogWarning(message), message => Log.LogError(message));
                Log.LogInfo("Japanese ship-name display translation active; units=" + shipNames.Translations.UnitCount + "; methods=" + shipCount);
            }
            catch (Exception error)
            {
                Log.LogError("Japanese ship-name display initialization failed: " + error);
            }
        }
        catch (Exception error)
        {
            Log.LogError("Japanese localization application failed: " + error);
        }
    }

    private static bool Enabled(string path)
    {
        var game = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Seapower-Scripts");
        var type = game.GetType("SeaPower.FileManager", true)!;
        return ModActivation.IsEnabled(type, path);
    }
}
