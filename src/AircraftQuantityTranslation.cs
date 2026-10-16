using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class AircraftQuantityTranslation
{
    public const string Target = "DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel/<>c__DisplayClass107_0::<AirGroupText>g__Count|2(System.ValueTuple`2<System.String,System.Int32>)";
    public const string Template = "{0} {1}";

    public static void TranslateIL(ILContext context, MethodBase original, TranslationCatalog catalog, Action<string>? logWarning = null)
    {
        if (MethodIdentity.Of(original) != Target) return;
        if (!catalog.Methods.TryGetValue(Target, out var literals) || !literals.ContainsKey(Template))
        {
            logWarning?.Invoke("Aircraft quantity display format is missing; skipped: " + Target);
            return;
        }
        var source = context.Body.Instructions.ToArray();
        var matches = Enumerable.Range(2, Math.Max(0, source.Length - 2)).Where(index =>
            source[index].OpCode == OpCodes.Call && source[index].Operand is MethodReference call &&
            call.DeclaringType.FullName == typeof(string).FullName && call.Name == nameof(string.Concat) &&
            call.Parameters.Count == 3 && call.Parameters.All(parameter => parameter.ParameterType.FullName == typeof(string).FullName) &&
            source[index - 1].OpCode == OpCodes.Ldfld && source[index - 1].Operand is FieldReference name &&
            name.DeclaringType.FullName == "DynamicCampaignMod.Core.Data.AircraftSpec" && name.Name == "DisplayName" &&
            source[index - 2].OpCode == OpCodes.Callvirt && source[index - 2].Operand is MethodReference aircraft &&
            aircraft.DeclaringType.FullName == "DynamicCampaignMod.Core.Data.IUnitCatalog" && aircraft.Name == "Aircraft" &&
            source.Take(index).LastOrDefault(instruction => instruction.OpCode == OpCodes.Ldstr)?.Operand is string separator && separator == " ").ToArray();
        if (matches.Length != 1)
        {
            logWarning?.Invoke("Aircraft quantity display construction was not recognized; skipped: " + Target);
            return;
        }

        // Replace only the final display join, retaining count selection, name lookup and control-flow targets.
        source[matches[0]].Operand = context.Import(typeof(OriginalModIntegration).GetMethod(nameof(OriginalModIntegration.AircraftQuantity))!);
    }
}
