using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class AircraftMessageTranslation
{
    public const string Target = "DynamicCampaignMod.Core.Sim.AlertLines::Add(System.String,System.String,System.String,System.Int32,System.String&)";
    public const string Template = "No {0} at {1}.";
    private static readonly OpCode[] MessagePattern =
    {
        OpCodes.Ldc_I4_5, OpCodes.Newarr,
        OpCodes.Dup, OpCodes.Ldc_I4_0, OpCodes.Ldstr, OpCodes.Stelem_Ref,
        OpCodes.Dup, OpCodes.Ldc_I4_1, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldarg_2, OpCodes.Callvirt, OpCodes.Stelem_Ref,
        OpCodes.Dup, OpCodes.Ldc_I4_2, OpCodes.Ldstr, OpCodes.Stelem_Ref,
        OpCodes.Dup, OpCodes.Ldc_I4_3, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldarg_1, OpCodes.Callvirt, OpCodes.Ldfld, OpCodes.Stelem_Ref,
        OpCodes.Dup, OpCodes.Ldc_I4_4, OpCodes.Ldstr, OpCodes.Stelem_Ref, OpCodes.Call
    };

    public static void TranslateIL(ILContext context, MethodBase original, TranslationCatalog catalog, Action<string>? logWarning = null)
    {
        if (MethodIdentity.Of(original) != Target) return;
        if (!catalog.Methods.TryGetValue(Target, out var literals) || !literals.TryGetValue(Template, out var format))
        {
            logWarning?.Invoke("Aircraft-absence message translation is missing; skipped: " + Target);
            return;
        }
        string.Format(format, "aircraft", "base");
        var source = context.Body.Instructions.ToArray();
        var matches = Enumerable.Range(0, Math.Max(0, source.Length - MessagePattern.Length + 1))
            .Where(index => Matches(source, index)).ToArray();
        if (matches.Length != 1)
        {
            logWarning?.Invoke("Aircraft-absence message generation was not recognized; skipped: " + Target);
            return;
        }
        var start = matches[0];
        if (source.Skip(start + 1).Take(MessagePattern.Length - 1).Any(instruction => HasBoundary(context, instruction)))
        {
            logWarning?.Invoke("Aircraft-absence message contains control-flow boundaries; skipped: " + Target);
            return;
        }

        // Keep the original name lookups and replace only the assembly of the display message.
        var kept = new HashSet<Instruction>(source.Skip(start + 9).Take(3).Concat(source.Skip(start + 19).Take(5)));
        source[start].OpCode = source[start + 8].OpCode;
        source[start].Operand = source[start + 8].Operand;
        var call = source[start + MessagePattern.Length - 1];
        call.OpCode = OpCodes.Call;
        call.Operand = context.Import(typeof(OriginalModIntegration).GetMethod(nameof(OriginalModIntegration.MissingAircraft))!);
        foreach (var instruction in source.Skip(start + 1).Take(MessagePattern.Length - 2))
            if (!kept.Contains(instruction)) context.IL.Remove(instruction);
    }

    private static bool HasBoundary(ILContext context, Instruction instruction) =>
        context.GetIncomingLabels(instruction).Any() || context.Body.Instructions.Any(source =>
            ReferenceEquals(source.Operand, instruction) || source.Operand is Instruction[] targets && targets.Contains(instruction)) ||
        context.Body.ExceptionHandlers.Any(handler => ReferenceEquals(handler.TryStart, instruction) || ReferenceEquals(handler.TryEnd, instruction) ||
            ReferenceEquals(handler.HandlerStart, instruction) || ReferenceEquals(handler.HandlerEnd, instruction) || ReferenceEquals(handler.FilterStart, instruction));

    private static bool Matches(IReadOnlyList<Instruction> source, int start)
    {
        for (var i = 0; i < MessagePattern.Length; i++)
            if (source[start + i].OpCode != MessagePattern[i]) return false;
        return source[start + 1].Operand is TypeReference element && element.FullName == typeof(string).FullName &&
            Equals(source[start + 4].Operand, "No ") && Equals(source[start + 15].Operand, " at ") && Equals(source[start + 27].Operand, ".") &&
            source[start + 9].Operand is FieldReference catalog && catalog.Name == "_catalog" && catalog.DeclaringType.FullName == "DynamicCampaignMod.Core.Sim.AlertLines" &&
            source[start + 11].Operand is MethodReference display && display.Name == "DisplayName" && display.DeclaringType.FullName == "DynamicCampaignMod.Core.Data.IUnitCatalog" &&
            source[start + 20].Operand is FieldReference state && state.Name == "_state" && state.DeclaringType.FullName == "DynamicCampaignMod.Core.Sim.AlertLines" &&
            source[start + 22].Operand is MethodReference getBase && getBase.Name == "Base" && getBase.DeclaringType.FullName == "DynamicCampaignMod.Core.State.CampaignState" &&
            source[start + 23].Operand is FieldReference name && name.Name == "name" && name.DeclaringType.FullName == "DynamicCampaignMod.Core.Data.BaseDefinition" &&
            source[start + 29].Operand is MethodReference concat && concat.DeclaringType.FullName == typeof(string).FullName && concat.Name == nameof(string.Concat) &&
            concat.Parameters.Count == 1 && concat.Parameters[0].ParameterType.FullName == typeof(string[]).FullName;
    }
}
