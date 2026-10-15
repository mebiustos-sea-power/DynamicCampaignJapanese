using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace DynamicCampaignJapanese;

public sealed class ShipDisplayIntegration
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const string HullType = "DynamicCampaignMod.Core.State.Hull";
    private const string CatalogType = "DynamicCampaignMod.Game.Units.UnitCatalog";
    private static ShipDisplayIntegration? current;
    private readonly ShipNameTranslations names;
    private readonly CampaignShipNames campaignNames;
    private readonly Func<bool> natoNames;
    private readonly FieldInfo hullName;
    private readonly FieldInfo hullUnit;
    private readonly FieldInfo hullVariant;
    private readonly MethodInfo originalTypeOf;
    private readonly Func<string, (string Abbreviation, string Kind, string Class)> typeOf;
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));

    private static readonly Dictionary<string, string[]> DirectTargets = new(StringComparer.Ordinal)
    {
        ["DynamicCampaignMod.UI.Base.BaseScreenViewModel"] = new[] { "ShipTile" },
        ["DynamicCampaignMod.UI.Strategy.FleetViewModel"] = new[] { "ShipRow", "FlightRow" },
        ["DynamicCampaignMod.UI.Strategy.ProcurementViewModel"] = new[] { "TypeText" },
        ["DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel"] = new[] { "Detail" },
        ["DynamicCampaignMod.UI.Merge.MergeViewModel"] = new[] { "Row" },
        ["DynamicCampaignMod.UI.Formation.FormationViewModel"] = new[] { "Refresh" },
        ["DynamicCampaignMod.UI.Formation.FormationPlot"] = new[] { "Draw" },
        ["DynamicCampaignMod.UI.Forces.ForcesViewModel"] = new[] { "Update" },
        ["DynamicCampaignMod.UI.Contact.DeploymentViewModel"] = new[] { ".ctor" },
        ["DynamicCampaignMod.UI.BottomBar.BottomBarViewModel"] = new[] { "FlightFields" },
        ["DynamicCampaignMod.UI.AirBase.CarrierAirViewModel"] = new[] { "get_Title" }
    };
    private static readonly Dictionary<string, string[]> CallbackTargets = new(StringComparer.Ordinal)
    {
        ["DynamicCampaignMod.UI.Map.OrdersMenu/"] = new[] { "<OpenOn>b__" },
        ["DynamicCampaignMod.UI.Forces.ForcesViewModel/"] = new[] { "<Update>b__" },
        ["DynamicCampaignMod.UI.Base.BaseScreenViewModel/"] = new[] { "<SummaryOf>b__", "<UpdateWing>b__" },
        ["DynamicCampaignMod.UI.Contact.DeploymentViewModel/"] = new[] { "<.ctor>b__" },
        ["DynamicCampaignMod.UI.AirBase.CarrierAirViewModel/"] = new[] { "<Refresh>b__" }
    };

    public ShipDisplayIntegration(Assembly original, ShipNameTranslations names, CampaignShipNames campaignNames, Func<bool> natoNames)
    {
        this.names = names;
        this.campaignNames = campaignNames;
        this.natoNames = natoNames;
        var hull = original.GetType(HullType, true)!;
        hullName = hull.GetField("Name") ?? throw new MissingFieldException(HullType, "Name");
        hullUnit = hull.GetField("UnitIni") ?? throw new MissingFieldException(HullType, "UnitIni");
        hullVariant = hull.GetField("Variant") ?? throw new MissingFieldException(HullType, "Variant");
        originalTypeOf = original.GetType(CatalogType, true)!.GetMethod("TypeOf", Declared, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException(CatalogType, "TypeOf");
        typeOf = (Func<string, (string, string, string)>)originalTypeOf.CreateDelegate(typeof(Func<string, (string, string, string)>));
    }

    public IReadOnlyList<MethodBase> Plan(Assembly original, Action<string>? logWarning = null)
    {
        var all = original.GetTypes().SelectMany(type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            .Where(method => method.GetMethodBody() != null).ToArray();
        foreach (var entry in DirectTargets)
            foreach (var method in entry.Value)
                if (!all.Any(candidate => TypeName(candidate) == entry.Key && candidate.Name == method))
                    logWarning?.Invoke("Ship display target not found; skipped: " + entry.Key + "::" + method);
        return all.Where(IsDisplayMethod).Where(ReadsShipDisplay).OrderBy(MethodIdentity.Of, StringComparer.Ordinal).ToArray();
    }

    public int Install(Assembly original, Harmony harmony, Action<string> logWarning, Action<string> logError)
    {
        var plan = Plan(original, logWarning);
        current = this;
        var patch = typeof(ShipDisplayIntegration).GetMethod(nameof(TranspileCurrent))!;
        var patched = 0;
        foreach (var method in plan)
        {
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(patch));
                patched++;
            }
            catch (Exception error)
            {
                logError("Cannot translate ship display in " + MethodIdentity.Of(method) + ": " + error);
                try { harmony.Unpatch(method, patch); }
                catch (Exception cleanupError) { logError("Cannot remove failed ship display patch: " + cleanupError); }
            }
        }
        return patched;
    }

    public string? NameOf(object hull)
    {
        var unit = (string?)hullUnit.GetValue(hull);
        var original = (string?)hullName.GetValue(hull);
        return campaignNames.TryTranslate(unit, original, out var translated)
            ? translated : names.Ship(unit, (string?)hullVariant.GetValue(hull), original);
    }
    public (string Abbreviation, string Kind, string Class) TypeOf(string unit)
    {
        var original = typeOf(unit);
        return (original.Abbreviation, original.Kind, names.Class(unit, original.Class, natoNames()));
    }

    public IEnumerable<CodeInstruction> TranslateInstructions(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var enabled = IsDisplayMethod(original);
        foreach (var source in instructions)
        {
            var instruction = new CodeInstruction(source);
            if (enabled && instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, hullName))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(ShipDisplayIntegration).GetMethod(nameof(DisplayName));
            }
            else if (enabled && instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                method.DeclaringType?.FullName == CatalogType && method.Name == "TypeOf")
                instruction.operand = typeof(ShipDisplayIntegration).GetMethod(nameof(DisplayType));
            yield return instruction;
        }
    }

    public static IEnumerable<CodeInstruction> TranspileCurrent(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
        Active().TranslateInstructions(instructions, __originalMethod);
    public static string? DisplayName(object hull) => Active().NameOf(hull);
    public static (string, string, string) DisplayType(string unit) => Active().TypeOf(unit);
    private static ShipDisplayIntegration Active() => current ?? throw new InvalidOperationException("Ship display translations have not been initialized.");
    private static string TypeName(MethodBase method) => method.DeclaringType!.FullName!.Replace('+', '/');
    private static bool IsDisplayMethod(MethodBase method)
    {
        var type = TypeName(method);
        return DirectTargets.TryGetValue(type, out var methods) && methods.Contains(method.Name, StringComparer.Ordinal) ||
            CallbackTargets.Any(entry => type.StartsWith(entry.Key, StringComparison.Ordinal) &&
                entry.Value.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private bool ReadsShipDisplay(MethodBase method)
    {
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort value = bytes[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            var opcode = OpCodesByValue[value];
            if (opcode == OpCodes.Ldfld && method.Module == hullName.Module && BitConverter.ToInt32(bytes, offset) == hullName.MetadataToken ||
                opcode == OpCodes.Call && method.Module == originalTypeOf.Module && BitConverter.ToInt32(bytes, offset) == originalTypeOf.MetadataToken)
                return true;
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                    OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                _ => throw new InvalidOperationException("Unsupported IL operand in " + MethodIdentity.Of(method) + ": " + opcode.OperandType)
            };
        }
        return false;
    }
}
