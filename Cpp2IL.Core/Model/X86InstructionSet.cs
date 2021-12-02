using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Model;

public class X86InstructionSet : BaseInstructionSet
{
    public override IControlFlowGraph BuildGraphForMethod(MethodAnalysisContext context)
    {
        var rawMethodBody = GetRawBytesForMethod(context, context is AttributeGeneratorMethodAnalysisContext);
        var methodBody = X86Utils.Disassemble(rawMethodBody, context.UnderlyingPointer);
        
        return new X86ControlFlowGraph(methodBody.ToList());
    }

    public override byte[] GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        if (!isAttributeGenerator)
            return X86Utils.GetRawManagedMethodBody(context.Definition!);
        
        X86Utils.GetMethodBodyAtVirtAddressNew(context.UnderlyingPointer, false, out var ret);
        return ret;
    }
}