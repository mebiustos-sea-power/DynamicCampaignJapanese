using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DynamicCampaignJapanese;
using HarmonyLib;
using Mono.Cecil;
using MonoMod.Cil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class QuantityDisplayChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const string AirBase = "DynamicCampaignMod.UI.AirBase.AirBaseViewModel";
    private const string Carrier = "DynamicCampaignMod.UI.AirBase.CarrierAirViewModel";
    private const string Capture = "DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel/<>c__DisplayClass105_0";
    private static readonly string[] Targets =
    {
        AirBase + "::<UpdateLoadoutDetails>b__122_0(System.Collections.Generic.KeyValuePair`2<System.String,System.Int32>)",
        Carrier + "::<UpdateLoadoutDetails>b__120_0(System.Collections.Generic.KeyValuePair`2<System.String,System.Int32>)",
        Capture + "::<AirGroupText>b__1(System.ValueTuple`2<System.String,System.Int32>)",
        AircraftQuantityTranslation.Target,
        AirBase + "::Count(System.Int32,System.String)",
        Carrier + "::Count(System.Int32,System.String)"
    };

    public static bool HasFormat(string identity) => Targets.Contains(identity, StringComparer.Ordinal);

    public static CodeInstruction[] Normalize(MethodBase method, CodeInstruction[] instructions)
    {
        if (MethodIdentity.Of(method) != AircraftQuantityTranslation.Target) return instructions;
        var concat = typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!;
        foreach (var instruction in instructions)
            if (instruction.operand is MethodInfo call && call.DeclaringType == typeof(OriginalModIntegration) &&
                call.Name == nameof(OriginalModIntegration.AircraftQuantity)) instruction.operand = concat;
        return instructions;
    }

    public static JObject Verify(Assembly original, OriginalModIntegration integration, TranslationCatalog catalog, List<string> errors)
    {
        var methods = original.GetTypes().SelectMany(type => type.GetMethods(All | BindingFlags.DeclaredOnly))
            .Where(method => method.GetMethodBody() != null).ToDictionary(MethodIdentity.Of);
        var proxy = (CatalogProxy)DispatchProxy.Create(original.GetType("DynamicCampaignMod.Core.Data.IUnitCatalog", true)!, typeof(CatalogProxy));
        object Create(string name) => Activator.CreateInstance(original.GetType(name.Replace('/', '+'), true)!, true)!;
        var aircraft = Create("DynamicCampaignMod.Core.Data.AircraftSpec");
        Set(aircraft, "IniName", "aircraft-internal-id");
        Set(aircraft, "DisplayName", "F-4J 試験機 {0}");
        proxy.Aircraft = aircraft;
        var sim = RuntimeHelpers.GetUninitializedObject(original.GetType("DynamicCampaignMod.Core.Sim.CampaignSim", true)!);
        Set(sim, "Catalog", proxy);
        object View(string name)
        {
            var view = RuntimeHelpers.GetUninitializedObject(original.GetType(name, true)!);
            Set(view, "_sim", sim);
            return view;
        }
        var details = View("DynamicCampaignMod.UI.Details.TaskForceDetailsViewModel");
        var capture = Create(Capture);
        Set(capture, "<>4__this", details);
        var aboard = (IList)Activator.CreateInstance(capture.GetType().GetField("aboard", All)!.FieldType)!;
        Set(capture, "aboard", aboard);
        var previous = typeof(OriginalModIntegration).GetField("current", All)!.GetValue(null);
        typeof(OriginalModIntegration).GetField("current", All)!.SetValue(null, integration);
        var cases = new JArray();
        var instructionsCompared = 0;
        try
        {
            foreach (var identity in Targets)
            {
                var method = methods[identity];
                var source = TranslationILChecks.Read(method);
                var translated = Normalize(method, TranslationILChecks.Read(method, integration).ToArray());
                if (source.Count != translated.Length) errors.Add("Quantity display changed instruction count: " + identity);
                for (var i = 0; i < Math.Min(source.Count, translated.Length); i++)
                {
                    instructionsCompared++;
                    if (source[i].opcode != translated[i].opcode || source[i].opcode != OpCodes.Ldstr && !Equals(source[i].operand, translated[i].operand) ||
                        !Metadata(source[i], "labels").Cast<object>().SequenceEqual(Metadata(translated[i], "labels").Cast<object>()) ||
                        !Metadata(source[i], "blocks").Cast<ExceptionBlock>().Select(b => (b.blockType, b.catchType))
                            .SequenceEqual(Metadata(translated[i], "blocks").Cast<ExceptionBlock>().Select(b => (b.blockType, b.catchType))))
                        errors.Add("Quantity display changed original control flow or data access: " + identity + " / " + i);
                }
                var output = Compile(method, integration);
                object? instance = method.DeclaringType!.FullName!.Contains("DisplayClass", StringComparison.Ordinal) ? capture :
                    method.DeclaringType!.FullName == AirBase ? View(AirBase) : View(Carrier);
                string Run(params object[] values) => (string)output.Invoke(null,
                    method.IsStatic ? values : new[] { instance }.Concat(values).ToArray())!;
                if (identity.Contains("UpdateLoadoutDetails", StringComparison.Ordinal))
                {
                    foreach (var count in new[] { 0, 2, 12 })
                    {
                        proxy.Requests.Clear();
                        var pair = new KeyValuePair<string, int>("weapon-internal-id", count);
                        var text = Run(pair);
                        if (text != proxy.WeaponName + " ×" + count || !proxy.Requests.SequenceEqual(new[] { "AmmoName:weapon-internal-id" }))
                            errors.Add("Weapon display changed its name lookup or quantity: " + identity + " / " + text);
                        cases.Add(new JObject { ["method"] = identity, ["quantity"] = count, ["display"] = text });
                    }
                }
                else if (identity.EndsWith("::Count(System.Int32,System.String)", StringComparison.Ordinal))
                {
                    foreach (var count in new[] { 0, 1, 21 })
                    {
                        proxy.Requests.Clear();
                        var text = Run(count, "aircraft-internal-id");
                        if (text != "F-4J 試験機 {0} ×" + count || !proxy.Requests.SequenceEqual(new[] { "DisplayName:aircraft-internal-id" }))
                            errors.Add("Aircraft list changed its internal-name lookup or count: " + text);
                        cases.Add(new JObject { ["method"] = identity, ["quantity"] = count, ["display"] = text });
                    }
                }
                else
                {
                    foreach (var scenario in new[] { (Full: 21, Aboard: 21), (Full: 21, Aboard: 18), (Full: 21, Aboard: 0), (Full: 0, Aboard: 0) })
                    {
                        aboard.Clear(); aboard.Add(("aircraft-internal-id", scenario.Aboard));
                        proxy.Requests.Clear();
                        var full = ("aircraft-internal-id", scenario.Full);
                        var dataBefore = JsonConvert.SerializeObject(new { Full = full, Aboard = aboard, Aircraft = aircraft });
                        var text = Run(full);
                        var count = identity == AircraftQuantityTranslation.Target && scenario.Aboard < scenario.Full
                            ? scenario.Aboard + "/" + scenario.Full : scenario.Full.ToString();
                        if (text != "F-4J 試験機 {0} ×" + count || !proxy.Requests.SequenceEqual(new[] { "Aircraft:aircraft-internal-id" }))
                            errors.Add("Carrier display changed the internal name or aboard/full selection: " + identity + " / " + text);
                        if (dataBefore != JsonConvert.SerializeObject(new { Full = full, Aboard = aboard, Aircraft = aircraft }))
                            errors.Add("Rendering a carrier quantity changed its data.");
                        cases.Add(new JObject { ["method"] = identity, ["full"] = scenario.Full, ["aboard"] = scenario.Aboard,
                            ["display"] = text, ["dataUnchanged"] = true });
                    }
                }
            }

            var target = methods[AircraftQuantityTranslation.Target];
            using var module = ModuleDefinition.ReadModule(target.Module.FullyQualifiedName);
            var changed = (MethodDefinition)module.LookupToken(target.MetadataToken);
            changed.Body.Instructions.Single(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && Equals(i.Operand, " ")).Operand = " / ";
            var unchanged = changed.Body.Instructions.Select(i => (i.OpCode, i.Operand)).ToArray();
            var warnings = new List<string>();
            using var context = new ILContext(changed);
            context.Invoke(il => AircraftQuantityTranslation.TranslateIL(il, target, catalog, warnings.Add));
            if (warnings.Count != 1 || !unchanged.SequenceEqual(changed.Body.Instructions.Select(i => (i.OpCode, i.Operand))))
                errors.Add("An unrecognized aircraft quantity display was changed or skipped without a warning.");
            return new JObject { ["scenarios"] = cases, ["instructionsCompared"] = instructionsCompared,
                ["unrecognizedDisplayLoggedAndPreserved"] = warnings.Count == 1 };
        }
        finally { typeof(OriginalModIntegration).GetField("current", All)!.SetValue(null, previous); }
    }

    private static MethodInfo Compile(MethodInfo method, OriginalModIntegration integration)
    {
        var parameters = method.GetParameters().Select(parameter => parameter.ParameterType);
        if (!method.IsStatic) parameters = new[] { method.DeclaringType! }.Concat(parameters);
        var result = new DynamicMethod("TranslatedQuantityDisplay", method.ReturnType, parameters.ToArray(), method.DeclaringType!, true);
        var generator = result.GetILGenerator();
        foreach (var local in method.GetMethodBody()!.LocalVariables) generator.DeclareLocal(local.LocalType);
        var code = TranslationILChecks.Read(method, integration).ToArray();
        var labels = code.SelectMany(instruction => Metadata(instruction, "labels").Cast<Label>()).Distinct().ToDictionary(label => label, _ => generator.DefineLabel());
        foreach (var instruction in code)
        {
            if (Metadata(instruction, "blocks").Count != 0) throw new InvalidOperationException("Quantity display fixture requires an exception-handler emitter.");
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
                case int index when instruction.opcode.OperandType == OperandType.ShortInlineVar: generator.Emit(instruction.opcode, checked((byte)index)); break;
                case int index when instruction.opcode.OperandType == OperandType.InlineVar: generator.Emit(instruction.opcode, checked((short)index)); break;
                case int value: generator.Emit(instruction.opcode, value); break;
                default: throw new InvalidOperationException("Unexpected quantity display instruction: " + instruction);
            }
        }
        return result;
    }

    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, All)!.SetValue(target, value);
    private static IList Metadata(CodeInstruction instruction, string name) => (IList)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!;

    public class CatalogProxy : DispatchProxy
    {
        public object Aircraft = null!;
        public string WeaponName = "AIM-9L（試験 {0}）";
        public readonly List<string> Requests = new();
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Requests.Add(method!.Name + ":" + arguments![0]);
            return method.Name switch
            {
                "Aircraft" => Aircraft,
                "DisplayName" => Aircraft.GetType().GetField("DisplayName")!.GetValue(Aircraft),
                "AmmoName" => WeaponName,
                _ => throw new InvalidOperationException("Unexpected quantity display lookup: " + method)
            };
        }
    }
}
