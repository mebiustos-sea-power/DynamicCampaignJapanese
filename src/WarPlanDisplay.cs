using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class WarPlanDisplay
{
    public const string Target = "DynamicCampaignMod.Core.Sim.Admiralty::WarPlanOrder(DynamicCampaignMod.Core.Data.WarPlanEntry,System.Int32,DynamicCampaignMod.Core.Data.BaseDefinition)";

    public static void TranslateIL(ILContext context, MethodBase original)
    {
        if (MethodIdentity.Of(original) != Target) return;
        foreach (var instruction in context.Body.Instructions.ToArray())
            if (instruction.Operand is FieldReference field && field.DeclaringType.FullName == "DynamicCampaignMod.Core.Data.WarPlanEntry" && field.Name == "name" &&
                instruction.Next?.Operand is MethodReference trim && trim.DeclaringType.FullName == "System.String" && trim.Name == "Trim")
                context.IL.InsertAfter(instruction.Next, Instruction.Create(OpCodes.Call,
                    context.Import(typeof(OriginalModIntegration).GetMethod(nameof(OriginalModIntegration.WarPlanRegion))!)));
    }
}
