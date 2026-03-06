using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;

namespace EditCompileReload;

internal class DefNormalizerImporter(MemberCloneContext context) : CloneContextAwareReferenceImporter(context)
{
    protected override ITypeDefOrRef ImportType(TypeReference type)
    {
        if (type.Scope?.GetAssembly() == null)
            return (TypeDefinition)Context.ClonedMembers[type.Resolve()!];

        return base.ImportType(type);
    }

    public override IMethodDefOrRef ImportMethod(IMethodDefOrRef method)
    {
        var imported = base.ImportMethod(method);

        return imported is MemberReference { DeclaringType: TypeDefinition } ?
            (IMethodDefOrRef)Context.ClonedMembers[method.Resolve()!] :
            imported;
    }

    public override IFieldDescriptor ImportField(IFieldDescriptor field)
    {
        var imported = base.ImportField(field);
        return imported is MemberReference { DeclaringType: TypeDefinition } ?
            (IFieldDescriptor)Context.ClonedMembers[field.Resolve()!] :
            imported;
    }
}
