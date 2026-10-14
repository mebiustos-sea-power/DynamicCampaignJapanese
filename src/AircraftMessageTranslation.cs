using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

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

    public static IEnumerable<CodeInstruction> TranslateInstructions(IEnumerable<CodeInstruction> instructions, MethodBase original,
        TranslationCatalog catalog, Action<string>? logWarning = null)
    {
        if (MethodIdentity.Of(original) != Target) return instructions;
        var source = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        if (!catalog.Methods.TryGetValue(Target, out var literals) || !literals.TryGetValue(Template, out var format))
        {
            logWarning?.Invoke("Aircraft-absence message translation is missing; skipped: " + Target);
            return source;
        }
        string.Format(format, "aircraft", "base");
        var matches = Enumerable.Range(0, Math.Max(0, source.Count - MessagePattern.Length + 1))
            .Where(index => Matches(source, index)).ToArray();
        if (matches.Length != 1)
        {
            logWarning?.Invoke("Aircraft-absence message generation was not recognized; skipped: " + Target);
            return source;
        }
        var start = matches[0];
        if (source.Skip(start + 1).Take(MessagePattern.Length - 1).Any(instruction => HasMetadata(instruction, "labels") || HasMetadata(instruction, "blocks")))
        {
            logWarning?.Invoke("Aircraft-absence message contains control-flow boundaries; skipped: " + Target);
            return source;
        }

        // Keep the original name lookups and replace only the assembly of the display message.
        var replacement = source.Skip(start + 8).Take(4).Concat(source.Skip(start + 19).Take(5))
            .Select(instruction => new CodeInstruction(instruction)).ToList();
        source[start].MoveLabelsTo(replacement[0]);
        source[start].MoveBlocksTo(replacement[0]);
        replacement.Add(new CodeInstruction(OpCodes.Call, typeof(OriginalModIntegration).GetMethod(nameof(OriginalModIntegration.MissingAircraft))));
        source.RemoveRange(start, MessagePattern.Length);
        source.InsertRange(start, replacement);
        return source;
    }

    private static bool HasMetadata(CodeInstruction instruction, string name) =>
        ((ICollection)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!).Count != 0;

    private static bool Matches(IReadOnlyList<CodeInstruction> source, int start)
    {
        for (var i = 0; i < MessagePattern.Length; i++)
            if (source[start + i].opcode != MessagePattern[i]) return false;
        return Equals(source[start + 1].operand, typeof(string)) &&
            Equals(source[start + 4].operand, "No ") && Equals(source[start + 15].operand, " at ") && Equals(source[start + 27].operand, ".") &&
            source[start + 9].operand is FieldInfo catalog && catalog.Name == "_catalog" && catalog.DeclaringType?.FullName == "DynamicCampaignMod.Core.Sim.AlertLines" &&
            source[start + 11].operand is MethodInfo display && display.Name == "DisplayName" && display.DeclaringType?.FullName == "DynamicCampaignMod.Core.Data.IUnitCatalog" &&
            source[start + 20].operand is FieldInfo state && state.Name == "_state" && state.DeclaringType?.FullName == "DynamicCampaignMod.Core.Sim.AlertLines" &&
            source[start + 22].operand is MethodInfo getBase && getBase.Name == "Base" && getBase.DeclaringType?.FullName == "DynamicCampaignMod.Core.State.CampaignState" &&
            source[start + 23].operand is FieldInfo name && name.Name == "name" && name.DeclaringType?.FullName == "DynamicCampaignMod.Core.Data.BaseDefinition" &&
            source[start + 29].operand is MethodInfo concat && concat.DeclaringType == typeof(string) && concat.Name == nameof(string.Concat) &&
            concat.GetParameters().Length == 1 && concat.GetParameters()[0].ParameterType == typeof(string[]);
    }
}
