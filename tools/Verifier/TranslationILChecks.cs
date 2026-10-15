using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using DynamicCampaignJapanese;
using HarmonyLib;
using Mono.Cecil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using CecilOperandType = Mono.Cecil.Cil.OperandType;
using MonoMod.Cil;
using MonoMod.Utils;

internal static class TranslationILChecks
{
    public static List<CodeInstruction> Read(MethodBase original, OriginalModIntegration? integration = null)
    {
        if (File.Exists(original.Module.FullyQualifiedName))
        {
            using var module = ModuleDefinition.ReadModule(original.Module.FullyQualifiedName);
            var method = (MethodDefinition)module.LookupToken(original.MetadataToken);
            return Translate(method, original, integration);
        }
        using var definition = new DynamicMethodDefinition("TranslationFixture", ((MethodInfo)original).ReturnType, Type.EmptyTypes);
        var generator = definition.GetILGenerator();
        foreach (var instruction in InstructionReader.Read(original))
        {
            if (instruction.operand is string text) generator.Emit(instruction.opcode, text);
            else if (instruction.operand == null) generator.Emit(instruction.opcode);
            else throw new InvalidOperationException("Unsupported in-memory translation fixture: " + instruction);
        }
        return Translate(definition.Definition, original, integration);
    }

    public static List<CodeInstruction> Translate(MethodDefinition method, MethodBase original, OriginalModIntegration? integration)
    {
        if (integration != null)
        {
            using var context = new ILContext(method);
            context.Invoke(il => integration.TranslateIL(il, original));
        }
        var generator = new DynamicMethod("ReadTranslationIL", typeof(void), Type.EmptyTypes).GetILGenerator();
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Name!, StringComparer.Ordinal);
        var codesByValue = codes.Values.ToDictionary(code => unchecked((ushort)code.Value));
        var labels = new Dictionary<CecilInstruction, Label>();
        CecilInstruction Target(object value) => value is ILLabel label ? label.Target : (CecilInstruction)value;
        Label Branch(object value)
        {
            var target = Target(value);
            if (!labels.TryGetValue(target, out var label)) labels.Add(target, label = generator.DefineLabel());
            return label;
        }
        var result = new List<CodeInstruction>();
        var mapped = new Dictionary<CecilInstruction, CodeInstruction>();
        foreach (var instruction in method.Body.Instructions)
        {
            var opcode = codesByValue[unchecked((ushort)instruction.OpCode.Value)];
            if (instruction.OpCode.OperandType == CecilOperandType.ShortInlineBrTarget) opcode = codes[instruction.OpCode.Name[..^2]];
            object? operand = instruction.Operand switch
            {
                CecilInstruction target => Branch(target),
                ILLabel label => Branch(label),
                CecilInstruction[] targets => targets.Select(target => Branch(target)).ToArray(),
                ILLabel[] targets => targets.Select(target => Branch(target)).ToArray(),
                ParameterDefinition parameter => parameter.Index + (method.HasThis ? 1 : 0),
                Mono.Cecil.Cil.VariableDefinition local => local.Index,
                TypeReference type => type.ResolveReflection(),
                FieldReference field => field.ResolveReflection(),
                MethodReference call => call.ResolveReflection(),
                _ => instruction.Operand
            };
            var output = new CodeInstruction(opcode, operand);
            result.Add(output);
            mapped.Add(instruction, output);
        }
        IList Metadata(CecilInstruction instruction, string name) => (IList)typeof(CodeInstruction).GetField(name)!.GetValue(mapped[instruction])!;
        foreach (var pair in labels) Metadata(pair.Key, "labels").Add(pair.Value);
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            Metadata(handler.TryStart, "blocks").Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            var kind = handler.HandlerType switch
            {
                Mono.Cecil.Cil.ExceptionHandlerType.Catch => ExceptionBlockType.BeginCatchBlock,
                Mono.Cecil.Cil.ExceptionHandlerType.Finally => ExceptionBlockType.BeginFinallyBlock,
                Mono.Cecil.Cil.ExceptionHandlerType.Fault => ExceptionBlockType.BeginFaultBlock,
                Mono.Cecil.Cil.ExceptionHandlerType.Filter => ExceptionBlockType.BeginExceptFilterBlock,
                _ => throw new InvalidOperationException("Unknown exception handler.")
            };
            Metadata(handler.FilterStart ?? handler.HandlerStart, "blocks").Add(new ExceptionBlock(kind, handler.CatchType?.ResolveReflection()));
            Metadata(handler.HandlerEnd?.Previous ?? method.Body.Instructions.Last(), "blocks").Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        }
        return result;
    }
}
