using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Attributes;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Plugin.ProtoDescriptorExtractor;
using LibCpp2IL.BinaryStructures;

[assembly: RegisterCpp2IlPlugin(typeof(ProtoDescriptorExtractorPlugin))]

namespace Cpp2IL.Plugin.ProtoDescriptorExtractor;

public class ProtoDescriptorExtractorPlugin : Cpp2IlPlugin
{
    public override string Name => "Protobuf file descriptor extractor";
    public override string Description => "Extracts file descriptors from reflection class static constructors";

    public override void OnLoad()
    {
        OutputFormatRegistry.Register<ProtoDescriptorOutputFormat>();
    }
}

file sealed record ReflectionTypeInfo(
    TypeAnalysisContext Type,
    FieldAnalysisContext Descriptor,
    MethodAnalysisContext Constructor);

public class ProtoDescriptorOutputFormat : Cpp2IlOutputFormat
{
    public override string OutputFormatId => "protodescriptor";
    public override string OutputFormatName => "Google.Protobuf file descriptors";

    public override void OnOutputFormatSelected()
    {
        StackAnalyzer.MaxBlockVisitCount = -1;
        MethodAnalysisContext.MaxMethodSizeBytes = -1;
    }

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        if (context.GetAssemblyByName("mscorlib.dll") is not { } mscorlib)
        {
            Logger.ErrorNewline("Failed to get mscorlib.dll assembly");
            return;
        }

        if (mscorlib.GetTypeByFullName("System.String") is not { } stringType)
        {
            Logger.ErrorNewline("Failed to get System.String type");
            return;
        }

        if (stringType.Methods.FirstOrDefault(x => x is
            {
                Name: "Concat", Parameters:
                [
                    {
                        Name: "values",
                        ParameterType: SzArrayTypeAnalysisContext
                        {
                            ElementType.Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING
                        }
                    }
                ]
            }) is not { } concatMethod)
        {
            Logger.ErrorNewline("Failed to get String::Concat method");
            return;
        }

        if (context.GetAssemblyByName("Google.Protobuf.dll") is not { } protobufAssembly)
        {
            Logger.ErrorNewline("Failed to get Google.Protobuf.dll assembly");
            return;
        }

        if (protobufAssembly.GetTypeByFullName("Google.Protobuf.Reflection.FileDescriptor") is not
            { } fileDescriptorType)
        {
            Logger.ErrorNewline("Failed to get Google.Protobuf.Reflection.FileDescriptor type");
            return;
        }

        var protobufReflectionTypes = new List<ReflectionTypeInfo>();
        foreach (var type in context.AllTypes.Where(x => x.IsStatic))
        {
            if (type.Fields.FirstOrDefault(field => field.FieldType == fileDescriptorType) is not { } descriptorField)
                continue;

            if (type.Methods.FirstOrDefault(method => method.Name == ".cctor") is not {} staticConstructor)
                continue;

            protobufReflectionTypes.Add(new ReflectionTypeInfo(type, descriptorField, staticConstructor));
            Logger.InfoNewline($"Found reflection type {type}");
        }

        Directory.CreateDirectory(outputRoot);

        Parallel.ForEach(protobufReflectionTypes, args =>
        {
            var (type, _, descriptorInitializer) = args;

            try
            {
                var descriptor = ResolveProtoDescriptor(descriptorInitializer, concatMethod, stringType);
                Logger.InfoNewline($"Retrieved descriptor for {type}");

                var outputPath = Path.Join(outputRoot, $"{type.FullName}.descriptor.pb");
                File.WriteAllBytes(outputPath, descriptor);
            }
            catch (Exception ex)
            {
                Logger.ErrorNewline($"Failed to retrieve descriptor for {type}: {ex}");
            }
        });
    }

    private static byte[] ResolveProtoDescriptor(MethodAnalysisContext constructor, MethodAnalysisContext concatMethod, TypeAnalysisContext stringType)
    {
        constructor.Analyze();

        LocalVariable? foundDescriptorArrayVariable = null;

        string[] descriptorBlobArray = [];
        var currentArrayIndex = 0ul;

        foreach (var inst in constructor.ConvertedIsil)
        {
            if (foundDescriptorArrayVariable == null)
            {
                if (IsCreateArrayInstruction(inst, out var descriptorArrayVariableRef, out var totalArraySize))
                {
                    foundDescriptorArrayVariable = descriptorArrayVariableRef;
                    descriptorBlobArray = new string[totalArraySize];
                }
            }
            else
            {
                if (IsAssignArrayElementInstruction(inst, out var partialDescriptor))
                {
                    descriptorBlobArray[currentArrayIndex++] = partialDescriptor;
                    continue;
                }

                if (IsConcatArrayInstruction(inst))
                {
                    Debug.Assert(descriptorBlobArray.Length == (int)currentArrayIndex);
                    if (descriptorBlobArray.Length != (int)currentArrayIndex)
                    {
                        Logger.WarnNewline($"Failed to read expected amount of descriptor lines for method {constructor}");
                    }

                    return Convert.FromBase64String(string.Concat(descriptorBlobArray));
                }
            }
        }

        throw new IndexOutOfRangeException("Failed to locate array concat instruction");

        bool IsCreateArrayInstruction(Instruction inst, [NotNullWhen(true)] out LocalVariable? descriptorArrayVariable, out ulong arraySize)
        {
            if (inst is
                {
                    OpCode: OpCode.Call,
                    Operands:
                    [
                        _, LocalVariable descriptorArrayVariableRef,
                        ArrayTypeAnalysisContext { ElementType: var elementType }, ulong arraySizeValue, _, _
                    ]
                }
                && elementType == stringType)
            {
                descriptorArrayVariable = descriptorArrayVariableRef;
                arraySize = arraySizeValue;
                return true;
            }

            descriptorArrayVariable = null;
            arraySize = 0;
            return false;
        }

        bool IsAssignArrayElementInstruction(Instruction inst, [NotNullWhen(true)] out string? partialDescriptor)
        {
            // This is the actual array move. Works for most methods.
            if (inst is
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        MemoryOperand { Base: LocalVariable descriptorArrayVariableRef2 }, string partialDescriptorValue2
                    ]
                } && foundDescriptorArrayVariable == descriptorArrayVariableRef2)
            {
                partialDescriptor = partialDescriptorValue2;
                return true;
            }

            // These are janky heuristics that only work due to ISIL being partially broken.

            if (inst is
                {
                    OpCode: OpCode.Call,
                    Operands:
                    [
                        _, _, MemoryOperand
                        {
                            Base: LocalVariable descriptorArrayVariableRef0
                        },
                        _,
                        string partialDescriptorValue, _
                    ]
                } && descriptorArrayVariableRef0 == foundDescriptorArrayVariable)
            {
                partialDescriptor = partialDescriptorValue;
                return true;
            }

            if (inst is
                {
                    OpCode: OpCode.Call,
                    Operands:
                    [
                        _, _, LocalVariable descriptorArrayVariableRef1, ulong targetArrayIndex,
                        string partialDescriptorValue1, _
                    ]
                } && descriptorArrayVariableRef1 == foundDescriptorArrayVariable
                  && targetArrayIndex == currentArrayIndex)
            {
                partialDescriptor = partialDescriptorValue1;
                return true;
            }

            partialDescriptor = null;
            return false;
        }

        bool IsConcatArrayInstruction(Instruction inst)
        {
            return inst is
                   {
                       OpCode: OpCode.Call,
                       Operands: [MethodAnalysisContext method, _, LocalVariable descriptorArrayVariableRef, _]
                   }
                   && method == concatMethod
                   && foundDescriptorArrayVariable == descriptorArrayVariableRef;
        }
    }
}
