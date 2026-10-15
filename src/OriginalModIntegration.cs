using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public sealed class OriginalModIntegration
{
    public const string XamlLoad = "DynamicCampaignMod.UI.Kit.XamlLoader::Load(System.String)";
    public const string XamlParse = "DynamicCampaignMod.UI.Kit.XamlLoader::Parse(System.String)";
    public const string CampaignLoad = "DynamicCampaignMod.Core.Data.CampaignDefinition::Load(System.String)";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static OriginalModIntegration? current;
    private readonly TranslationCatalog catalog;
    private readonly MarkupTranslator markup;
    private readonly CampaignTranslator campaign;
    private Action<string>? logWarning;

    public OriginalModIntegration(TranslationCatalog catalog)
    {
        this.catalog = catalog;
        markup = new MarkupTranslator(catalog);
        campaign = new CampaignTranslator(catalog);
    }

    public IReadOnlyList<MethodBase> Plan(Assembly assembly, Action<string>? logWarning = null)
    {
        var index = assembly.GetTypes().SelectMany(type =>
            type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(method => method.GetMethodBody() != null).ToDictionary(MethodIdentity.Of, StringComparer.Ordinal);
        var signatures = new HashSet<string>(catalog.Methods.Keys, StringComparer.Ordinal)
        {
            XamlLoad, XamlParse, CampaignLoad
        };
        var plan = new List<MethodBase>();
        foreach (var signature in signatures.OrderBy(value => value, StringComparer.Ordinal))
            if (index.TryGetValue(signature, out var method)) plan.Add(method);
            else logWarning?.Invoke("Translation target not found; skipped: " + signature);
        return plan;
    }

    public int Install(Assembly assembly, Harmony harmony, Action<string>? logWarning = null)
    {
        var plan = Plan(assembly, logWarning);
        this.logWarning = logWarning;
        current = this;
        var manipulator = new HarmonyMethod(typeof(OriginalModIntegration).GetMethod(nameof(ManipulateCurrent)));
        var patched = 0;
        try
        {
            foreach (var method in plan)
            {
                if (method.IsConstructor && method.IsStatic) continue;
                try { harmony.Patch(method, ilmanipulator: manipulator); }
                catch (Exception error) { throw new InvalidOperationException("Cannot apply Japanese translation to " + MethodIdentity.Of(method), error); }
                patched++;
            }
            CachedDisplayTranslations.Apply(assembly, catalog, logWarning);
            return patched;
        }
        catch (Exception error)
        {
            current = null;
            try { harmony.UnpatchSelf(); }
            catch (Exception rollbackError) { throw new AggregateException("Japanese localization failed and its patches could not be removed.", error, rollbackError); }
            throw;
        }
    }

    public void TranslateIL(ILContext context, MethodBase original)
    {
        var identity = MethodIdentity.Of(original);
        catalog.Methods.TryGetValue(identity, out var literals);
        var isMarkup = identity == XamlLoad || identity == XamlParse;
        var isCampaign = identity == CampaignLoad;
        AircraftMessageTranslation.TranslateIL(context, original, catalog, logWarning);
        foreach (var instruction in context.Body.Instructions.ToArray())
        {
            // Edit the existing instructions so branch targets and exception boundaries remain intact.
            if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text &&
                literals != null && literals.TryGetValue(text, out var translated)) instruction.Operand = translated;

            if (isMarkup && instruction.Operand is MethodReference parse && parse.DeclaringType.FullName == "Noesis.GUI" && parse.Name == "ParseXaml")
            {
                var parseCall = Instruction.Create(instruction.OpCode, parse);
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = context.Import(typeof(OriginalModIntegration).GetMethod(nameof(TranslateMarkup))!);
                context.IL.InsertAfter(instruction, parseCall);
            }
            if (isCampaign && instruction.Operand is MethodReference read && read.DeclaringType.FullName == typeof(File).FullName &&
                read.Name == nameof(File.ReadAllText) && read.Parameters.Count == 1)
                instruction.Operand = context.Import(typeof(OriginalModIntegration).GetMethod(nameof(ReadCampaign))!);
        }
    }

    public static void ManipulateCurrent(ILContext context, MethodBase original) => Active().TranslateIL(context, original);

    public static string MissingAircraft(string aircraftName, string baseName) =>
        string.Format(Active().catalog.Methods[AircraftMessageTranslation.Target][AircraftMessageTranslation.Template], aircraftName, baseName);

    public static string TranslateMarkup(string value) => Active().markup.Translate(value);
    public static string ReadCampaign(string path) => Active().campaign.Translate(File.ReadAllText(path));
    private static OriginalModIntegration Active() => current ?? throw new InvalidOperationException("Japanese localization has not been initialized.");
}
