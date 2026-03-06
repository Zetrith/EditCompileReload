using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AsmResolver.DotNet.Signatures;

namespace EditCompileReload;

// MemberReference(TypeReference, string, MethodSignature)
// MemberReference(TypeSpecification(GenericInstanceTypeSignature(TypeReference, bool, TypeSignature[])), string, MethodSignature) : Class<int>.Test()
// MethodSpecification(IMethodDefOrRef, GenericInstanceMethodSignature(IEnumerable<TypeSignature>)) : Test<int>()

// TypeReference(ModuleDefinition?, IResolutionScope?, Utf8String?, Utf8String?)
// TypeReference(ModuleDefinition?, AssemblyReference, Utf8String?, Utf8String?)
// TypeReference(ModuleDefinition?, TypeReference, Utf8String?, Utf8String?)

// GenericInstanceTypeSignature(ITypeDefOrRef, bool, IEnumerable<TypeSignature>)
// MethodSignature(CallingConventionAttributes, TypeSignature, IEnumerable<TypeSignature>?)

// Dob means date of birth
// The cloner is used as a general purpose metadata graph walker
internal class DobCloneImporter(AssemblyDefinition origAsm, MemberCloneContext context)
    : CloneContextAwareReferenceImporter(context)
{
    protected override ITypeDefOrRef ImportType(TypeDefinition type)
    {
        return ImportTypeDefRefOrSpec(type).Item1;
    }

    protected override ITypeDefOrRef ImportType(TypeReference type)
    {
        return ImportTypeDefRefOrSpec(type).Item1;
    }

    public override TypeSignature ImportTypeSignature(TypeSignature type)
    {
        return type.AcceptVisitor(this);
    }

    private (ITypeDefOrRef, int, bool?) ImportTypeDefRefOrSpec(ITypeDescriptor type, int typeDob = -1)
    {
        if (typeDob != -1 && type is TypeSpecification { Signature: var sig })
        {
            if (sig is GenericInstanceTypeSignature gits)
                return (new TypeSpecification(
                    new GenericInstanceTypeSignature(
                        ImportTypeDefRefOrSpec(gits.GenericType, typeDob).Item1,
                        gits.IsValueType,
                        gits.TypeArguments.ToArray()
                    )
                ), typeDob, gits.IsValueType);

            throw new Exception($"Unhandled TypeSpec case for type {type}");
        }

        var scopeAssembly = type.Scope?.GetAssembly() ?? origAsm;
        var typeString = type.ToString();

        if (type.Scope is not ModuleReference &&
            AsmStore.assemblyData.TryGetValue(scopeAssembly.Name, out var data) &&
            (typeDob != -1 || data.types.ContainsKey(typeString)))
        {
            bool? isValueType = null;
            if (data.types.TryGetValue(typeString, out var dataType))
            {
                if (typeDob == -1)
                    typeDob = dataType.Item1;
                isValueType = dataType.Item2;
            }

            var typeRef = new TypeReference(
                TargetModule,
                type.Scope is ITypeDefOrRef parentType
                    ?
                    (IResolutionScope)ImportTypeDefRefOrSpec(parentType, typeDob).Item1
                    : typeDob == data.version && scopeAssembly.Name == origAsm.Name
                        ? null
                        :
                        new AssemblyReference(scopeAssembly)
                        {
                            Name = scopeAssembly.Name + (typeDob == 0 ? "" : typeDob.ToString())
                        },
                type.Namespace,
                type.Name
            );

            return (typeRef, typeDob, isValueType);
        }

        return (type switch
        {
            null => throw new ArgumentNullException(nameof(type)),
            TypeDefinition definition => base.ImportType(definition),
            TypeReference reference => base.ImportType(reference),
            TypeSpecification specification => base.ImportType(specification),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        }, -1, type is TypeSpecification { Signature: var s } ? s!.IsValueType : null);
    }

    public static string GetMethodFullName(IMethodDescriptor reference)
    {
        var state = new StringBuilder();
        var signature = reference.Signature;

        MemberNameGenerator.AppendTypeFullName(state, signature?.ReturnType);
        state.Append(' ');
        MemberNameGenerator.AppendMemberDeclaringType(state, reference.DeclaringType);
        state.Append(reference.Name ?? "<<<Null name>>>");

        state.Append('`');
        state.Append(signature?.GenericParameterCount ?? 0);

        state.Append('(');
        MemberNameGenerator.AppendSignatureParameterTypes(state, signature);
        state.Append(')');

        return state.ToString();
    }

    private static ITypeDefOrRef MakeOpenGeneric(ITypeDefOrRef? type)
    {
        return type switch
        {
            null => throw new ArgumentNullException(nameof(type)),
            TypeDefinition => type,
            TypeReference => type,
            TypeSpecification { Signature: GenericInstanceTypeSignature { GenericType: var genericType } } =>
                genericType,
            TypeSpecification spec => spec,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    public override IMethodDefOrRef ImportMethod(IMethodDefOrRef method)
    {
        var methodForData = new MemberReference(
            MakeOpenGeneric(method.DeclaringType),
            method.Name,
            ImportMethodSignature(method.Signature)
        );

        {
            if (method.DeclaringType.Scope is not ModuleReference &&
                method.DeclaringType.Scope.GetAssembly() is { } scopeAsm &&
                AsmStore.assemblyData.TryGetValue(scopeAsm.Name, out var data) &&
                data.methods.TryGetValue(GetMethodFullName(methodForData), out var methodDob))
            {
                var signature = ImportMethodSignature(method.Signature);
                if (signature.HasThis)
                {
                    var thisTypeForData = ImportTypeDefRefOrSpec(methodForData.DeclaringType!);
                    var thisType = ImportTypeDefRefOrSpec(method.DeclaringType);
                    if (thisType.Item1 is TypeSpecification { Signature: GenericInstanceTypeSignature gits })
                    {
                        thisType.Item1 = new TypeSpecification(
                            new GenericInstanceTypeSignature(
                                gits.GenericType,
                                gits.IsValueType,
                                gits.TypeArguments.
                                    Select(TypeSignature (_, i) => new GenericParameterSignature(GenericParameterType.Type, i)).
                                    ToArray()
                            )
                        );
                    }

                    if (thisTypeForData.Item2 < methodDob)
                    {
                        signature.HasThis = false;
                        var newThisType =
                            thisTypeForData.Item3!.Value
                                ? new ByReferenceTypeSignature(thisType.Item1.ToTypeSignature(true))
                                : thisType.Item1.ToTypeSignature();
                        signature.ParameterTypes.Insert(0, newThisType);
                    }
                }

                return new MemberReference(
                    ImportTypeDefRefOrSpec(method.DeclaringType, methodDob).Item1,
                    method.Name,
                    signature
                );
            }
        }

        return base.ImportMethod(method);
    }

    public override IFieldDescriptor ImportField(IFieldDescriptor field)
    {
        var fieldForData = new MemberReference(
            MakeOpenGeneric((ITypeDefOrRef)field.DeclaringType), // todo is this cast valid?
            field.Name,
            ImportFieldSignature(field.Signature)
        );

        if (field.DeclaringType.Scope is not ModuleReference &&
            field.DeclaringType.Scope.GetAssembly() is { } scopeAsm &&
            AsmStore.assemblyData.TryGetValue(scopeAsm.Name, out var data) &&
            data.fields.TryGetValue(fieldForData.ToString(), out var fieldDob))
        {
            return new MemberReference(
                ImportTypeDefRefOrSpec(field.DeclaringType, fieldDob).Item1,
                field.Name,
                ImportFieldSignature(field.Signature)
            );
        }
        return base.ImportField(field);
    }
}
