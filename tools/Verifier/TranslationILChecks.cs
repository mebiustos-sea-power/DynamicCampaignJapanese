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
    public static MethodInfo Compile(MethodBase method, OriginalModIntegration? integration,
        Func<CodeInstruction[], CodeInstruction[]>? rewrite = null, Type? returnType = null, Type[]? parameterTypes = null)
    {
        var parameters = method.GetParameters().Select(parameter => parameter.ParameterType);
        if (!method.IsStatic) parameters = new[] { method.DeclaringType!.IsValueType ? method.DeclaringType.MakeByRefType() : method.DeclaringType }.Concat(parameters);
        var result = new DynamicMethod("TranslatedBehaviorFixture", returnType ?? (method is MethodInfo function ? function.ReturnType : typeof(void)), parameterTypes ?? parameters.ToArray(), method.Module, true);
        var generator = result.GetILGenerator();
        foreach (var local in method.GetMethodBody()!.LocalVariables) generator.DeclareLocal(local.LocalType);
        var code = TranslationILChecks.Read(method, integration).ToArray();
        if (rewrite != null) code = rewrite(code);
        var labels = code.SelectMany(instruction => Metadata(instruction, "labels").Cast<Label>()).Distinct().ToDictionary(label => label, _ => generator.DefineLabel());
        foreach (var instruction in code)
        {
            if (Metadata(instruction, "blocks").Count != 0) throw new InvalidOperationException("Behavior fixture requires an exception-handler emitter.");
            foreach (var label in Metadata(instruction, "labels").Cast<Label>()) generator.MarkLabel(labels[label]);
            switch (instruction.operand)
            {
                case null: generator.Emit(instruction.opcode); break;
                case string text: generator.Emit(instruction.opcode, text); break;
                case Type type: generator.Emit(instruction.opcode, type); break;
                case FieldInfo field: generator.Emit(instruction.opcode, field); break;
                case MethodInfo call: generator.Emit(instruction.opcode, call); break;
                case ConstructorInfo constructor: generator.Emit(instruction.opcode, constructor); break;
                case Label label: generator.Emit(instruction.opcode, labels[label]); break;
                case Label[] targets: generator.Emit(instruction.opcode, targets.Select(target => labels[target]).ToArray()); break;
                case int index when instruction.opcode.OperandType == OperandType.ShortInlineVar: generator.Emit(instruction.opcode, checked((byte)index)); break;
                case int index when instruction.opcode.OperandType == OperandType.InlineVar: generator.Emit(instruction.opcode, checked((short)index)); break;
                case int value: generator.Emit(instruction.opcode, value); break;
                case sbyte value: generator.Emit(instruction.opcode, value); break;
                case byte value: generator.Emit(instruction.opcode, value); break;
                case long value: generator.Emit(instruction.opcode, value); break;
                case float value: generator.Emit(instruction.opcode, value); break;
                case double value: generator.Emit(instruction.opcode, value); break;
                default: throw new InvalidOperationException("Unexpected behavior fixture instruction: " + instruction);
            }
        }
        return result;
    }

    private static IList Metadata(CodeInstruction instruction, string name) => (IList)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!;
}
