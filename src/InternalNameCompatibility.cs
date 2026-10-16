using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class InternalNameCompatibility
{
    public const string Detachment = "DynamicCampaignMod.Core.Sim.AirOperations::DetachmentStem(System.String)";
    public const string Reroute = "DynamicCampaignMod.Game.Battle.MissionWriter::RerouteOf(System.String)";
    public const string Scattered = "DynamicCampaignMod.Game.Battle.BattleSession::NoteScattered()";

    public static string? DetachmentName(string? name)
    {
        if (name == null || name.IndexOf("分遣隊", StringComparison.Ordinal) < 0) return name;
        var match = Regex.Match(name, @"^(.*?)(?: 分遣隊|分遣隊(?=\d))(\d*)$");
        return match.Success ? match.Groups[1].Value + " det." + (match.Groups[2].Length == 0 ? "" : " " + match.Groups[2].Value) : name;
    }

    public static string? TriggerName(string? name) => name != null && name.EndsWith("が散開", StringComparison.Ordinal)
        ? name.Substring(0, name.Length - "が散開".Length) + " scatters" : name;

    public static void TranslateIL(ILContext context, MethodBase original)
    {
        var identity = MethodIdentity.Of(original);
        if (identity == Detachment || identity == Reroute)
        {
            var first = context.Body.Instructions[0];
            var transform = context.Import(typeof(InternalNameCompatibility).GetMethod(identity == Detachment ? nameof(DetachmentName) : nameof(TriggerName))!);
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Call, transform));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, context.Method.Parameters[0]));
        }
        else if (identity == Scattered)
        {
            foreach (var instruction in context.Body.Instructions.ToArray())
                if (instruction.Operand is MethodReference getter && getter.DeclaringType.FullName == "SeaPower.Trigger" && getter.Name == "get_Name")
                    context.IL.InsertAfter(instruction, Instruction.Create(OpCodes.Call, context.Import(typeof(InternalNameCompatibility).GetMethod(nameof(TriggerName))!)));
        }
    }
}
