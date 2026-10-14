using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

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

    public OriginalModIntegration(TranslationCatalog catalog)
    {
        this.catalog = catalog;
        markup = new MarkupTranslator(catalog);
        campaign = new CampaignTranslator(catalog);
    }

    public IReadOnlyList<MethodBase> Plan(Assembly assembly)
    {
        if (assembly.GetName().Name != "DynamicCampaignMod" || assembly.GetName().Version != catalog.OriginalVersion)
            throw new InvalidOperationException("Unsupported Dynamic Campaign assembly: " + assembly.GetName());
        var index = assembly.GetTypes().SelectMany(type =>
            type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(method => method.GetMethodBody() != null).ToDictionary(MethodIdentity.Of, StringComparer.Ordinal);
        var signatures = new HashSet<string>(catalog.Methods.Keys, StringComparer.Ordinal)
        {
            XamlLoad, XamlParse, CampaignLoad
        };
        foreach (var signature in signatures)
            if (!index.ContainsKey(signature)) throw new MissingMethodException("Translation target not found: " + signature);
        return signatures.OrderBy(value => value, StringComparer.Ordinal).Select(signature => index[signature]).ToArray();
    }

    public int Install(Assembly assembly, Harmony harmony)
    {
        var plan = Plan(assembly);
        current = this;
        var transpiler = new HarmonyMethod(typeof(OriginalModIntegration).GetMethod(nameof(TranspileCurrent)));
        var patched = 0;
        try
        {
            foreach (var method in plan)
            {
                if (method.IsConstructor && method.IsStatic) continue;
                try { harmony.Patch(method, transpiler: transpiler); }
                catch (Exception error) { throw new InvalidOperationException("Cannot apply Japanese translation to " + MethodIdentity.Of(method), error); }
                patched++;
            }
            CachedDisplayTranslations.Apply(assembly, catalog);
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

    public IEnumerable<CodeInstruction> TranslateInstructions(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var identity = MethodIdentity.Of(original);
        catalog.Methods.TryGetValue(identity, out var literals);
        var isMarkup = identity == XamlLoad || identity == XamlParse;
        var isCampaign = identity == CampaignLoad;
        foreach (var source in instructions)
        {
            var instruction = new CodeInstruction(source);
            if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string text &&
                literals != null && literals.TryGetValue(text, out var translated)) instruction.operand = translated;

            if (isMarkup && instruction.operand is MethodInfo parse && parse.DeclaringType?.FullName == "Noesis.GUI" && parse.Name == "ParseXaml")
            {
                var translate = new CodeInstruction(OpCodes.Call, typeof(OriginalModIntegration).GetMethod(nameof(TranslateMarkup)));
                instruction.MoveLabelsTo(translate);
                instruction.MoveBlocksTo(translate);
                yield return translate;
            }
            if (isCampaign && instruction.operand is MethodInfo read && read.DeclaringType == typeof(File) &&
                read.Name == nameof(File.ReadAllText) && read.GetParameters().Length == 1)
                instruction.operand = typeof(OriginalModIntegration).GetMethod(nameof(ReadCampaign));
            yield return instruction;
        }
    }

    public static IEnumerable<CodeInstruction> TranspileCurrent(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        return Active().TranslateInstructions(instructions, __originalMethod);
    }

    public static string TranslateMarkup(string value) => Active().markup.Translate(value);
    public static string ReadCampaign(string path) => Active().campaign.Translate(File.ReadAllText(path));
    private static OriginalModIntegration Active() => current ?? throw new InvalidOperationException("Japanese localization has not been initialized.");
}
