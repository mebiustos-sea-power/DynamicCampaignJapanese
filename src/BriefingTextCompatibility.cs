using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DynamicCampaignJapanese;

public static class BriefingTextCompatibility
{
    public const string Parse = "DynamicCampaignMod.Core.Sim.Briefing::Parse(System.String)";
    public const string IsOpening = "DynamicCampaignMod.Core.Sim.Story::IsOpening(DynamicCampaignMod.Core.State.SideMessage)";
    public const string Sections = "DynamicCampaignMod.Game.Battle.MissionWriter/<>c::<Text>b__12_3(System.ValueTuple`2<System.String,System.Collections.Generic.List`1<System.String>>)";
    public const string OrderLines = "DynamicCampaignMod.Game.Battle.MissionWriter/<>c::<Text>b__12_5(System.String)";
    private static readonly string[] Headers = { "From: ", "To: ", "Info: ", "SUBJ: " };
    private static readonly string[][] PreviousHeaders =
    {
        new[] { "発：", "発信：" }, new[] { "宛：", "宛先：" }, new[] { "通報先：" }, new[] { "件名：" }
    };

    public static string? Normalize(string? text)
    {
        if (text == null || !PreviousHeaders[0].Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < Headers.Length) return text;
        for (var index = 0; index < Headers.Length; index++)
        {
            var previous = PreviousHeaders[index].FirstOrDefault(prefix => lines[index].StartsWith(prefix, StringComparison.Ordinal));
            if (previous == null) return text;
            lines[index] = Headers[index] + lines[index].Substring(previous.Length).TrimStart(' ');
        }
        for (var index = Headers.Length; index < lines.Length; index++)
            if (Regex.IsMatch(lines[index], @"^\d+\. \(S\) .*：$")) lines[index] = lines[index].Substring(0, lines[index].Length - 1) + ":";
        return string.Join("\n", lines);
    }

    public static string SectionKey(string value) => value == "状況" ? "SITUATION" : value == "任務" ? "MISSION" : value;

    public static string OrderLine(string value)
    {
        foreach (var prefix in new[] { "海軍司令部命令：", "海軍司令部命令: " })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return "Admiralty order: " + value.Substring(prefix.Length);
        return value;
    }

    public static void TranslateIL(ILContext context, MethodBase original)
    {
        var identity = MethodIdentity.Of(original);
        if (identity == OrderLines)
        {
            var first = context.Body.Instructions[0];
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, context.Method.Parameters[0]));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Call, context.Import(typeof(BriefingTextCompatibility).GetMethod(nameof(OrderLine))!)));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, context.Method.Parameters[0]));
            return;
        }
        if (identity == Sections)
        {
            foreach (var instruction in context.Body.Instructions.ToArray())
                if (instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field && field.Name == "Item1")
                    context.IL.InsertAfter(instruction, Instruction.Create(OpCodes.Call, context.Import(typeof(BriefingTextCompatibility).GetMethod(nameof(SectionKey))!)));
            return;
        }
        if (identity != Parse && identity != IsOpening) return;
        var normalize = context.Import(typeof(BriefingTextCompatibility).GetMethod(nameof(Normalize))!);
        if (identity == Parse)
        {
            var first = context.Body.Instructions[0];
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Call, normalize));
            context.IL.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, context.Method.Parameters[0]));
        }
        else
        {
            foreach (var instruction in context.Body.Instructions.ToArray())
                if (instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field &&
                    field.DeclaringType.FullName == "DynamicCampaignMod.Core.State.SideMessage" && field.Name == "Text")
                    context.IL.InsertAfter(instruction, Instruction.Create(OpCodes.Call, normalize));
        }
    }
}
