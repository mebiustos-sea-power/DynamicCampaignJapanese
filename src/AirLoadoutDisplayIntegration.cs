using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace DynamicCampaignJapanese;

public sealed class AirLoadoutDisplayIntegration
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const string LoadoutType = "DynamicCampaignMod.Core.Data.LoadoutSpec";
    private const string PickerType = "DynamicCampaignMod.UI.AirBase.PickerItem";
    private const string ShipyardLoadoutDisplay = "DynamicCampaignMod.UI.Strategy.ProcurementViewModel/<>c::<RefreshDetail>b__162_10(DynamicCampaignMod.Core.Data.LoadoutSpec)";
    private static readonly string[] Views =
    {
        "DynamicCampaignMod.UI.AirBase.AirBaseViewModel",
        "DynamicCampaignMod.UI.AirBase.CarrierAirViewModel"
    };
    private static AirLoadoutDisplayIntegration? current;
    private readonly GameLoadoutNames names;
    private Action<string>? logWarning;

    public AirLoadoutDisplayIntegration(GameLoadoutNames names) => this.names = names;

    public IReadOnlyList<MethodInfo> Plan(Assembly original, Action<string>? logWarning = null)
    {
        var plan = original.GetTypes().SelectMany(type => type.GetMethods(Declared))
            .Where(method => IsLoadoutFactory(method) || IsShipyardLoadoutDisplay(method))
            .OrderBy(MethodIdentity.Of, StringComparer.Ordinal).ToArray();
        foreach (var view in Views)
            if (!plan.Any(method => method.DeclaringType!.FullName!.StartsWith(view + "+", StringComparison.Ordinal)))
                logWarning?.Invoke("Loadout display target not found; skipped: " + view + "::UpdateLoadouts");
        if (!plan.Any(IsShipyardLoadoutDisplay))
            logWarning?.Invoke("Loadout display target not found; skipped: " + ShipyardLoadoutDisplay);
        return plan;
    }

    public int Install(Assembly original, Harmony harmony, Action<string> logWarning, Action<string> logError)
    {
        this.logWarning = logWarning;
        current = this;
        var patch = typeof(AirLoadoutDisplayIntegration).GetMethod(nameof(TranspileCurrent))!;
        var patched = 0;
        foreach (var method in Plan(original, logWarning))
        {
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(patch));
                patched++;
            }
            catch (Exception error)
            {
                logError("Cannot translate aircraft loadout display in " + MethodIdentity.Of(method) + ": " + error);
                try { harmony.Unpatch(method, patch); }
                catch (Exception cleanupError) { logError("Cannot remove failed loadout display patch: " + cleanupError); }
            }
        }
        return patched;
    }

    public IEnumerable<CodeInstruction> TranslateInstructions(IEnumerable<CodeInstruction> instructions, MethodBase original,
        Action<string>? logWarning = null)
    {
        var result = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        if (IsShipyardLoadoutDisplay(original))
        {
            if (result.Count != 3 || result[0].opcode != OpCodes.Ldarg_1 || !ReadsName(result[1]) || result[2].opcode != OpCodes.Ret)
            {
                logWarning?.Invoke("Shipyard loadout display construction was not recognized; skipped: " + MethodIdentity.Of(original));
                return result;
            }
            // This selector supplies the shipyard's display list without changing LoadoutSpec.Name.
            result.Insert(2, new CodeInstruction(OpCodes.Call, typeof(AirLoadoutDisplayIntegration).GetMethod(nameof(DisplayName))));
            return result;
        }
        if (!IsLoadoutFactory(original)) return result;
        var matches = new List<int>();
        for (var index = 0; index + 3 < result.Count; index++)
            if (result[index].opcode == OpCodes.Ldarg_1 && ReadsName(result[index + 1]) &&
                result[index + 2].opcode == OpCodes.Ldarg_1 && ReadsName(result[index + 3])) matches.Add(index + 3);
        var constructors = result.Select((instruction, index) => (instruction, index)).Where(item =>
            item.instruction.opcode == OpCodes.Newobj && item.instruction.operand is ConstructorInfo constructor &&
            constructor.DeclaringType?.FullName == PickerType &&
            constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(Enumerable.Repeat(typeof(string), 4))).ToArray();
        if (matches.Count != 1 || result.Count(ReadsName) != 2 || constructors.Length != 1 || matches[0] >= constructors[0].index)
        {
            logWarning?.Invoke("Loadout display construction was not recognized; skipped: " + MethodIdentity.Of(original));
            return result;
        }
        // PickerItem's first argument is the internal key; only its second argument is a display name.
        result.Insert(matches[0] + 1, new CodeInstruction(OpCodes.Call, typeof(AirLoadoutDisplayIntegration).GetMethod(nameof(DisplayName))));
        return result;
    }

    public static IEnumerable<CodeInstruction> TranspileCurrent(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
        Active().TranslateInstructions(instructions, __originalMethod, Active().logWarning);
    public static string DisplayName(string key) => Active().names.DisplayName(key);
    private static AirLoadoutDisplayIntegration Active() => current ?? throw new InvalidOperationException("Loadout display translations have not been initialized.");
    private static bool ReadsName(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldfld &&
        instruction.operand is FieldInfo field && field.DeclaringType?.FullName == LoadoutType && field.Name == "Name" && field.FieldType == typeof(string);
    private static bool IsShipyardLoadoutDisplay(MethodBase method) => MethodIdentity.Of(method) == ShipyardLoadoutDisplay;
    private static bool IsLoadoutFactory(MethodBase method) => method is MethodInfo info && info.ReturnType.FullName == PickerType &&
        info.GetParameters().Length == 1 && info.GetParameters()[0].ParameterType.FullName == LoadoutType &&
        info.Name.StartsWith("<UpdateLoadouts>b__", StringComparison.Ordinal) && Views.Any(view =>
            info.DeclaringType!.FullName!.StartsWith(view + "+", StringComparison.Ordinal));
}
