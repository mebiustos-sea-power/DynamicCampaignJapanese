using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class BattleReportDisplay
{
    public const string Target = "DynamicCampaignMod.UI.Report.BattleReportViewModel::.ctor(DynamicCampaignMod.Core.Sim.CampaignSim,DynamicCampaignMod.Core.State.BattleReport,System.Int32,System.Action)";

    public static void TranslateIL(ILContext context, MethodBase original)
    {
        if (MethodIdentity.Of(original) != Target) return;
        foreach (var instruction in context.Body.Instructions.ToArray())
            if (instruction.Operand is MethodReference getter && getter.DeclaringType.FullName == "DynamicCampaignMod.Core.State.BattleReport" && getter.Name == "get_Outcome" &&
                instruction.Next?.Operand is MethodReference upper && upper.Name == "ToUpperInvariant" &&
                instruction.Next.Next?.Operand is FieldReference field && field.DeclaringType.FullName == "DynamicCampaignMod.UI.Report.BattleReportViewModel" && field.Name == "<Outcome>k__BackingField")
                context.IL.InsertAfter(instruction, Instruction.Create(OpCodes.Call,
                    context.Import(typeof(OriginalModIntegration).GetMethod(nameof(OriginalModIntegration.BattleOutcome))!)));
    }
}
