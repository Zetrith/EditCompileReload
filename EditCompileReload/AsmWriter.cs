using System.Collections.Concurrent;
using System.Diagnostics;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Cloning;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Serialized;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssemblyDefinition = AsmResolver.DotNet.AssemblyDefinition;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using FieldDefinition = AsmResolver.DotNet.FieldDefinition;
using GenericParameter = AsmResolver.DotNet.GenericParameter;
using GenericParameterType = AsmResolver.DotNet.Signatures.GenericParameterType;
using MemberReference = AsmResolver.DotNet.MemberReference;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using ModuleDefinition = AsmResolver.DotNet.ModuleDefinition;
using SecurityDeclaration = AsmResolver.DotNet.SecurityDeclaration;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;
using TypeDefinition = AsmResolver.DotNet.TypeDefinition;
using TypeSpecification = AsmResolver.DotNet.TypeSpecification;

namespace EditCompileReload;

public static class AsmWriter
{
    public static void UpdateMemberDobs(AssemblyDefinition asm)
    {
        if (!AsmStore.assemblyData.TryGetValue(asm.Name!, out var asmData))
            asmData = AsmStore.assemblyData[asm.Name!] = new AssemblyData();

        var allTypes = asm.ManifestModule.GetAllTypes().
            Select(t => t.ToString()).
            ToHashSet();
        var allMethods = asm.ManifestModule.GetAllTypes().
            SelectMany(m => m.Methods).
            Select(DobCloneImporter.GetMethodFullName).
            ToHashSet();
        var allFields = asm.ManifestModule.GetAllTypes().
            SelectMany(m => m.Fields).
            Select(f => f.ToString()).
            ToHashSet();

        foreach (var t in asm.ManifestModule.GetAllTypes())
            if (!asmData.types.ContainsKey(t.ToString()) || IsCompilerGeneratedRecursive(t))
                asmData.types[t.ToString()] = (asmData.version, t.IsValueType);

        foreach (var t in asm.ManifestModule.GetAllTypes())
            foreach (var m in t.Methods)
                if (!asmData.methods.ContainsKey(DobCloneImporter.GetMethodFullName(m))
                    || IsCompilerGeneratedRecursive(t)
                    || m.IsCompilerGenerated())
                {
                    // New constructor on existing type
                    if (m.IsConstructor && asmData.types[t.ToString()].Item1 != asmData.version)
                        ; // todo can't handle this case
                    asmData.methods[DobCloneImporter.GetMethodFullName(m)] = asmData.version;
                }

        foreach (var t in asm.ManifestModule.GetAllTypes())
            foreach (var f in t.Fields)
                if (!asmData.fields.ContainsKey(f.ToString())
                    || IsCompilerGeneratedRecursive(t)
                    || (f.IsCompilerGenerated() && !f.Name!.ToString().EndsWith("__BackingField")))
                    asmData.fields[f.ToString()] = asmData.version;

        // Remove deleted members

        foreach (var t in asmData.types.Keys.ToList())
            if (!allTypes.Contains(t))
                asmData.types.Remove(t);

        foreach (var m in asmData.methods.Keys.ToList())
            if (!allMethods.Contains(m))
                asmData.methods.Remove(m);

        foreach (var f in asmData.fields.Keys.ToList())
            if (!allFields.Contains(f))
                asmData.fields.Remove(f);
    }

    private static bool IsCompilerGeneratedRecursive(TypeDefinition type)
    {
        if (type.IsCompilerGenerated())
            return true;

        if (type.DeclaringType != null)
            return IsCompilerGeneratedRecursive(type.DeclaringType);

        return false;
    }

    private static int ptrGetters;

    public static (byte[], Dictionary<string, uint>) RewriteOriginal(string path)
    {
        var watch = Stopwatch.StartNew();
        var assembly =
            AssemblyDefinition.FromImage(
                PEImage.FromFile(path),
                new ModuleReaderParameters(Path.GetDirectoryName(path), EmptyErrorListener.Instance));

        var mainModule = assembly.ManifestModule!;

        var ptrsType = new TypeDefinition("ECR", "Ptrs",
            TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public,
            mainModule.CorLibTypeFactory.Object.Type);
        var ptrArrField = new FieldDefinition(
            "arr",
            FieldAttributes.Static | FieldAttributes.Public,
            mainModule.CorLibTypeFactory.IntPtr.MakeSzArrayType());
        ptrsType.Fields.Add(ptrArrField);

        var getMethodPtr =
            new CorLibReplaceImporter(mainModule).ImportMethod(
                typeof(AsmStore).GetMethod(nameof(AsmStore.GetMethodPtr))!);
        var stElemAndReturn =
            new CorLibReplaceImporter(mainModule).ImportMethod(
                typeof(AsmWriter).GetMethod(nameof(StElemAndReturn))!);
        var runtimeTypeHandleType = new CorLibReplaceImporter(mainModule).ImportType(typeof(RuntimeTypeHandle));
        var objTypeSig = mainModule.CorLibTypeFactory.Object.Type.ToTypeSignature();

        int maxGenericParameters = mainModule.TopLevelTypes
            .SelectMany(t => t.Methods)
            .Max(m => (m.DeclaringType?.GenericParameters.Count ?? 0) + m.GenericParameters.Count);
        for (var i = 0; i < maxGenericParameters; i++)
            ptrsType.GenericParameters.Add(new GenericParameter($"T{i}"));
        mainModule.TopLevelTypes.Add(ptrsType);

        EcrLog.Message($"Assembly read {watch.GetMillisAndRestart()}");

        int methodIndex = 0;
        var implToOldOrigRid = new ConcurrentDictionary<string, uint>();

        foreach (var type in mainModule.GetAllTypes())
        {
            ProcessType(type);
        }

        void ProcessType(TypeDefinition type)
        {
            if (type.Name == "<Module>") return;
            if (type.IsInterface) return;

            foreach (var m in type.Methods.ToList())
            {
                if (m.CilMethodBody == null) continue;

                // var ptrFieldForMethod = new FieldDefinition(
                //     DobCloneImporter.GetMethodFullName(m),
                //     FieldAttributes.Static | FieldAttributes.Public,
                //     mainModule.CorLibTypeFactory.IntPtr);
                //
                // ptrsType.Fields.Add(ptrFieldForMethod);

                var originalMethodBody = m.CilMethodBody;
                m.CilMethodBody = new CilMethodBody();

                var cloneResult = new MemberCloner(
                    mainModule,
                    _ =>
                        new CloneContextAwareReferenceImporter(new MemberCloneContext(mainModule))
                ).Include(m).Clone();

                var clonedMethod = cloneResult.GetClonedMember(m);
                clonedMethod.CilMethodBody = originalMethodBody;
                clonedMethod.CustomAttributes.Clear(); // Don't duplicate attributes of original method
                AsmHelper.TurnInstanceIntoStatic(clonedMethod, m.Parameters.ThisParameter?.ParameterType);
                clonedMethod.Name += "_Impl";
                clonedMethod.Attributes &= ~MethodAttributes.SpecialName;
                clonedMethod.Attributes &= ~MethodAttributes.RuntimeSpecialName;
                clonedMethod.Attributes &= ~MethodAttributes.Virtual;
                clonedMethod.Attributes &= ~MethodAttributes.NewSlot;
                clonedMethod.Attributes &= ~MethodAttributes.Final;
                clonedMethod.Attributes &= ~MethodAttributes.Abstract;
                type.Methods.Add(clonedMethod);

                implToOldOrigRid[DobCloneImporter.GetMethodFullName(clonedMethod)] = m.MetadataToken.Rid;

                WriteOrigMethodBody(
                    methodIndex++,
                    m,
                    m.CilMethodBody,
                    clonedMethod,
                    maxGenericParameters,
                    mainModule,
                    mainModule,
                    ptrsType,
                    getMethodPtr,
                    stElemAndReturn,
                    runtimeTypeHandleType,
                    objTypeSig
                );
            }
        }

        var ptrsCctor = MakePtrsCctor(methodIndex, ptrsType, mainModule);
        ptrsType.Methods.Add(ptrsCctor);

        EcrLog.Message($"Method clone {watch.GetMillisAndRestart()}");

        var stream = new MemoryStream();
        WriteAssemblyToStream(assembly, stream);
        EcrLog.Message($"Write to stream {watch.GetMillisAndRestart()}");

        return (stream.ToArray(), implToOldOrigRid.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private static void WriteOrigMethodBody(
        int methodIndex,
        MethodDefinition origMethod,
        CilMethodBody origMethodBody,
        MethodDefinition clonedMethodImpl,
        int maxGenericParameters,
        ModuleDefinition mainModule,
        ModuleDefinition moduleToAddTo,
        ITypeDefOrRef fieldsType,
        IMethodDescriptor getMethodPtr,
        IMethodDescriptor stElemAndReturn,
        ITypeDefOrRef runtimeTypeHandleType,
        TypeSignature objTypeSig)
    {
        var type = origMethod.DeclaringType;

        if (origMethod.Parameters.ThisParameter != null)
            origMethodBody.Instructions.Add(
                CilOpCodes.Ldarg,
                origMethod.Parameters.ThisParameter
            );

        foreach (var param in origMethod.Parameters)
        {
            origMethodBody.Instructions.Add(
                CilOpCodes.Ldarg,
                param
            );
        }

        // var ptr = ptrArr[methodIndex]
        // if (ptr == IntPtr.Zero)
        //      ptr = ptrArr[methodIndex] = GetMethodPtr("sgn", ldtoken(cloned), ldtoken(declaringType), ldtoken(ptrsType), genericArgumentsFromType, genericArgumentsFromMethod)
        // (*ptr)(...)
        // return

        // ldsfld ptrArr
        // ldc.i4 methodIndex
        // ldelem.i
        // dup
        // brtrue CALL
        // pop
        // ldstr sgn
        // ldtoken cloned
        // ldtoken declaringType

        // Example for genericArgumentsFromType:
        // ldc.i4 2
        // newarr RuntimeTypeHandle
        // dup
        // ldc.i4 0
        // ldtoken !!0
        // stelem RuntimeTypeHandle
        // dup
        // ldc.i4 1
        // ldtoken !!1
        // stelem RuntimeTypeHandle

        // call GetMethodPtr
        // ldsfld ptrArr
        // ldc.i4 methodIndex
        // call StElemAndReturn
        // CALL: calli
        // ret

        var typeArgs = new List<TypeSignature>();

        for (int i = 0; i < type.GenericParameters.Count; i++)
            typeArgs.Add(new GenericParameterSignature(GenericParameterType.Type, i));

        for (int i = 0; i < origMethod.GenericParameters.Count; i++)
            typeArgs.Add(new GenericParameterSignature(GenericParameterType.Method, i));

        int fillerTypeCount = maxGenericParameters - type.GenericParameters.Count - origMethod.GenericParameters.Count;
        for (int i = 0; i < fillerTypeCount; i++)
            typeArgs.Add(objTypeSig);

        var ptrArrField = new MemberReference(
            new TypeSpecification(fieldsType.MakeGenericInstanceType(typeArgs.ToArray())),
            "arr",
            new FieldSignature(mainModule.CorLibTypeFactory.IntPtr.MakeSzArrayType()));

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldsfld,
            ptrArrField
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldc_I4,
            methodIndex
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldelem_I
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Dup
        );

        var callLabel = new CilInstructionLabel();
        origMethodBody.Instructions.Add(
            CilOpCodes.Brtrue,
            callLabel
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Pop
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldstr,
            DobCloneImporter.GetMethodFullName(origMethod)
        );

        ITypeDefOrRef declaringTypeSpec =
            clonedMethodImpl.DeclaringType.HasGenericParameters
                ? new TypeSpecification(clonedMethodImpl.DeclaringType.MakeGenericInstanceType(type.GenericParameters.Select(
                    TypeSignature (_, i) => new GenericParameterSignature(GenericParameterType.Type, i)).ToArray()))
                : clonedMethodImpl.DeclaringType;

        IMethodDefOrRef methodWithTypeSpec =
            clonedMethodImpl.DeclaringType.HasGenericParameters ?
                new MemberReference(declaringTypeSpec, clonedMethodImpl.Name, clonedMethodImpl.Signature) :
                clonedMethodImpl;

        IMethodDescriptor methodSpec = clonedMethodImpl.HasGenericParameters
            ? methodWithTypeSpec.MakeGenericInstanceMethod(
                origMethod.GenericParameters.Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(GenericParameterType.Method, i)).ToArray())
            : methodWithTypeSpec;

        var ptrGetter = MakeAndAddPtrGetter(clonedMethodImpl, clonedMethodImpl.DeclaringType, moduleToAddTo, origMethod.DeclaringModule);

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldtoken,
            ptrGetter
        );

        if (ptrGetter.DeclaringType != null)
            declaringTypeSpec =
                ptrGetter.DeclaringType.HasGenericParameters ?
                new TypeSpecification(ptrGetter.DeclaringType.MakeGenericInstanceType(type.GenericParameters.Select(
                        TypeSignature (_, i) => new GenericParameterSignature(GenericParameterType.Type, i)).ToArray()))
                    : ptrGetter.DeclaringType;

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldtoken,
            declaringTypeSpec
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldtoken,
            new TypeSpecification(fieldsType.MakeGenericInstanceType(typeArgs.ToArray()))
        );

        WriteGenericArgumentStoreInstructions(
            origMethodBody,
            type.GenericParameters.Count,
            GenericParameterType.Type,
            runtimeTypeHandleType
        );

        WriteGenericArgumentStoreInstructions(
            origMethodBody,
            origMethod.GenericParameters.Count,
            GenericParameterType.Method,
            runtimeTypeHandleType
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Call,
            getMethodPtr
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldsfld,
            ptrArrField
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Ldc_I4,
            methodIndex
        );

        origMethodBody.Instructions.Add(
            CilOpCodes.Call,
            stElemAndReturn
        );

        callLabel.Instruction = origMethodBody.Instructions.Add(
            CilOpCodes.Calli,
            methodSpec.Signature.MakeStandAloneSignature()
        );

        origMethodBody.Instructions.Add(CilOpCodes.Ret);
        methodIndex++;
    }

    public static IntPtr StElemAndReturn(IntPtr value, IntPtr[] arr, int index)
    {
        arr[index] = value;
        return value;
    }

    private static void WriteGenericArgumentStoreInstructions(
        CilMethodBody body,
        int genericParametersCount,
        GenericParameterType genericParameterType,
        ITypeDefOrRef runtimeTypeHandleType)
    {
        if (genericParametersCount == 0)
        {
            body.Instructions.Add(
                CilOpCodes.Ldnull
            );
        }
        else
        {
            body.Instructions.Add(
                CilOpCodes.Ldc_I4,
                genericParametersCount
            );

            body.Instructions.Add(
                CilOpCodes.Newarr,
                runtimeTypeHandleType
            );

            for (int i = 0; i < genericParametersCount; i++)
            {
                body.Instructions.Add(
                    CilOpCodes.Dup
                );

                body.Instructions.Add(
                    CilOpCodes.Ldc_I4,
                    i
                );

                body.Instructions.Add(
                    CilOpCodes.Ldtoken,
                    new TypeSpecification(new GenericParameterSignature(genericParameterType, i))
                );

                body.Instructions.Add(
                    CilOpCodes.Stelem,
                    runtimeTypeHandleType
                );
            }
        }
    }

    private static MethodDefinition MakePtrsCctor(int arrSize, TypeDefinition ptrsType, ModuleDefinition newModule)
    {
        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
            MethodAttributes.RuntimeSpecialName | MethodAttributes.SpecialName,
            new MethodSignature(
                CallingConventionAttributes.Default,
                newModule.CorLibTypeFactory.Void, null)
        );

        var typeArgs = new List<TypeSignature>();

        for (int i = 0; i < ptrsType.GenericParameters.Count; i++)
            typeArgs.Add(new GenericParameterSignature(GenericParameterType.Type, i));

        var ptrArrField = new MemberReference(
            new TypeSpecification(ptrsType.MakeGenericInstanceType(typeArgs.ToArray())),
            "arr",
            new FieldSignature(newModule.CorLibTypeFactory.IntPtr.MakeSzArrayType()));

        cctor.CilMethodBody = new CilMethodBody();

        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, arrSize);
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Newarr, newModule.CorLibTypeFactory.IntPtr.Type);
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Stsfld, ptrArrField);
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

        return cctor;
    }

    private static MethodDefinition MakeAndAddPtrGetter(MethodDefinition of, TypeDefinition toType, ModuleDefinition toModule, ModuleDefinition origModule)
    {
        var ptrGetter = new MethodDefinition(
            of.Name + "_PtrGetter" + ++ptrGetters,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            new MethodSignature(
                of.HasGenericParameters ? CallingConventionAttributes.Generic : CallingConventionAttributes.Default,
                origModule.CorLibTypeFactory.IntPtr, null)
            {
                GenericParameterCount = of.GenericParameters.Count
            }
        );

        foreach (var genericParam in of.GenericParameters)
            ptrGetter.GenericParameters.Add(new GenericParameter(genericParam.Name));

        var declaringTypeSpec = new TypeSpecification(
            of.DeclaringType.HasGenericParameters
                ? of.DeclaringType.MakeGenericInstanceType(of.DeclaringType.GenericParameters.Select(
                    TypeSignature (_, i) => new GenericParameterSignature(GenericParameterType.Type, i)).ToArray())
                : of.DeclaringType.ToTypeSignature());

        IMethodDefOrRef methodWithTypeSpec =
            of.DeclaringType.HasGenericParameters ?
                new MemberReference(declaringTypeSpec, of.Name, of.Signature) :
                of;

        IMethodDescriptor methodSpec = of.HasGenericParameters
            ? methodWithTypeSpec.MakeGenericInstanceMethod(
                of.GenericParameters.Select(TypeSignature (_, i) =>
                    new GenericParameterSignature(GenericParameterType.Method, i)).ToArray())
            : methodWithTypeSpec;

        ptrGetter.CilMethodBody = new CilMethodBody();

        ptrGetter.CilMethodBody.Instructions.Add(CilOpCodes.Ldftn, methodSpec);
        ptrGetter.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

        // Prevent "Cannot invoke method with stack pointers via reflection" exceptions on Mono
        if (toType.IsByRefLike)
        {
            var newType = new TypeDefinition(
                toType.Namespace,
                toType.Name + ptrGetters,
                TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public,
                toModule.CorLibTypeFactory.Object.Type
            );

            for (var i = 0; i < toType.GenericParameters.Count; i++)
                newType.GenericParameters.Add(new GenericParameter(toType.GenericParameters[i].Name));

            newType.Methods.Add(ptrGetter);
            toModule.TopLevelTypes.Add(newType);
        }
        else
        {
            toType.Methods.Add(ptrGetter);
        }

        return ptrGetter;
    }

    public static (byte[], Dictionary<string, string>, Dictionary<string, uint>) RewriteChanged(AssemblyDefinition assembly)
    {
        var watch = Stopwatch.StartNew();
        EcrLog.Message($"Start write changed {assembly.Name}");

        var version = AsmStore.assemblyData[assembly.Name].version;
        var newAsm = new AssemblyDefinition( $"{assembly.Name}{version}", assembly.Version);
        foreach (var sec in assembly.SecurityDeclarations)
            newAsm.SecurityDeclarations.Add(new SecurityDeclaration(sec.Action, sec.PermissionSet));

        var asmData = AsmStore.assemblyData[assembly.Name];
        var existingMethods = asmData.methods;
        var existingTypes = asmData.types;

        var mainModule = assembly.ManifestModule!;
        var newModule = new ModuleDefinition($"{mainModule.Name}{version}");
        newAsm.Modules.Add(newModule);

        var ptrsType = new TypeDefinition("ECR", "Ptrs",
            TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
            newModule.CorLibTypeFactory.Object.Type);
        var ptrArrField = new FieldDefinition(
            "arr",
            FieldAttributes.Static | FieldAttributes.Public,
            mainModule.CorLibTypeFactory.IntPtr.MakeSzArrayType());
        ptrsType.Fields.Add(ptrArrField);

        var ignoresAccessChecksToCtor =
            mainModule.TopLevelTypes.
                FirstOrDefault(t => t.Name == "IgnoresAccessChecksToAttribute")?.
                GetConstructor(mainModule.CorLibTypeFactory.String) ?? MakeIgnoresAccessChecksTo();

        newAsm.CustomAttributes.Add(new CustomAttribute(
            ignoresAccessChecksToCtor,
            new CustomAttributeSignature(new CustomAttributeArgument(mainModule.CorLibTypeFactory.String, assembly.Name))
        ));

        var getMethodPtr =
            new CorLibReplaceImporter(mainModule).ImportMethod(
                typeof(AsmStore).GetMethod(nameof(AsmStore.GetMethodPtr))!);
        var stElemAndReturn =
            new CorLibReplaceImporter(mainModule).ImportMethod(
                typeof(AsmWriter).GetMethod(nameof(StElemAndReturn))!);
        var runtimeTypeHandleType = new CorLibReplaceImporter(mainModule).ImportType(typeof(RuntimeTypeHandle));
        var objTypeSig = mainModule.CorLibTypeFactory.Object.Type.ToTypeSignature();

        int maxGenericParameters = mainModule.TopLevelTypes
            .SelectMany(t => t.Methods)
            .Max(m => (m.DeclaringType?.GenericParameters.Count ?? 0) + m.GenericParameters.Count);
        for (var i = 0; i < maxGenericParameters; i++)
            ptrsType.GenericParameters.Add(new GenericParameter($"T{i}"));
        newModule.TopLevelTypes.Add(ptrsType);

        var ptrGetterToOrig = new Dictionary<MethodDefinition, string>();
        var implToOldOrigRid = new Dictionary<string, uint>();
        int methodIndex = 0;

        foreach (var t in mainModule.TopLevelTypes)
        {
            if (t.Name == "<Module>") continue;
            HandleType(t);
        }

        var ptrsCctor = MakePtrsCctor(methodIndex, ptrsType, newModule);
        ptrsType.Methods.Add(ptrsCctor);

        EcrLog.Message($"Method clone {watch.GetMillisAndRestart()}");

        NormalizeDefinitions();

        EcrLog.Message($"Definition normalization {watch.GetMillisAndRestart()}");

        // foreach (var t in newModule.TopLevelTypes)
        //     foreach (var c in t.CustomAttributes)
        //         if (c.Constructor is MemberReference { DeclaringType.Scope: null })
        //             c.Constructor = new MemberReference(
        //                 c.Constructor.DeclaringType.Resolve(),
        //                 c.Constructor.Name,
        //                 c.Constructor.Signature);

        var stream = new MemoryStream();
        WriteAssemblyToStream(newAsm, stream);

        EcrLog.Message($"Write to stream {watch.GetMillisAndRestart()}");

        // var nas = AssemblyDefinition.FromBytes(stream.ToArray());
        // foreach (var t in nas.ManifestModule.TopLevelTypes)
        //     new MemberCloner(nas.ManifestModule, ctx => new HelperImporter(ctx)).Include(t).Clone();

        return (
            stream.ToArray(),
            ptrGetterToOrig.ToDictionary(
                p => DobCloneImporter.GetMethodFullName(p.Key),
                p => p.Value
            ),
            implToOldOrigRid
        );

        void NormalizeDefinitions()
        {
            var normalizerCloner = new MemberCloner(
                newModule,
                ctx => new DefNormalizerImporter(ctx)
            );

            foreach (var t in newModule.TopLevelTypes)
                normalizerCloner.Include(t);

            var normalizerCloneResult = normalizerCloner.Clone();
            newModule.TopLevelTypes.Clear();

            foreach (var clonedType in normalizerCloneResult.ClonedTopLevelTypes)
                newModule.TopLevelTypes.Add(clonedType);
        }

        MethodDefinition MakeIgnoresAccessChecksTo()
        {
            var ignoresAccessChecksToType = new TypeDefinition(
                "System.Runtime.CompilerServices",
                "IgnoresAccessChecksToAttribute",
                TypeAttributes.Class,
                new TypeReference(
                    mainModule.CorLibTypeFactory.CorLibScope,
                    "System",
                    "Attribute"
                )
            );
            var ignoresAccessChecksToCtorDef = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.RuntimeSpecialName | MethodAttributes.SpecialName,
                new MethodSignature(
                    CallingConventionAttributes.HasThis,
                    mainModule.CorLibTypeFactory.Void,
                    [mainModule.CorLibTypeFactory.String])
            )
            {
                CilMethodBody = new CilMethodBody
                {
                    Instructions = { CilOpCodes.Ret }
                }
            };
            ignoresAccessChecksToType.Methods.Add(ignoresAccessChecksToCtorDef);
            newModule.TopLevelTypes.Add(ignoresAccessChecksToType);
            return ignoresAccessChecksToCtorDef;
        }

        TypeDefinition? HandleType(TypeDefinition type)
        {
            var existingTypeStub =
                existingTypes.TryGetValue(type.ToString(), out var typeDob) && typeDob.Item1 == asmData.version ?
                    HandleNewType(type) :
                    HandleExistingType(type);

            foreach (var nestedType in type.NestedTypes)
            {
                var clonedNestedType = HandleType(nestedType);
                if (clonedNestedType != null)
                    existingTypeStub.NestedTypes.Add(clonedNestedType);
            }

            if (!type.IsNested)
                newModule.TopLevelTypes.Add(existingTypeStub);

            return existingTypeStub;
        }

        TypeDefinition HandleExistingType(TypeDefinition type)
        {
            var existingTypeStub = new TypeDefinition(
                type.Namespace,
                type.Name,
                type.Attributes,
                type.IsValueType ?
                    new TypeReference(
                        type.DeclaringModule.CorLibTypeFactory.CorLibScope,
                        "System",
                        "ValueType"
                    ) :
                    type.DeclaringModule.CorLibTypeFactory.Object.Type
            );

            foreach (var genericParam in type.GenericParameters)
                existingTypeStub.GenericParameters.Add(new GenericParameter(genericParam.Name));

            foreach (var field in type.Fields.ToList())
            {
                var clonedField = new MemberCloner(
                        newModule,
                        _ => new DobCloneImporter(assembly, new MemberCloneContext(newModule)))
                    .Include(field).Clone().GetClonedMember(field);
                existingTypeStub.Fields.Add(clonedField);
            }

            if (type.IsInterface)
                return existingTypeStub;

            foreach (var m in type.Methods.ToList())
            {
                if (m.CilMethodBody == null) continue;

                // New method
                if (existingMethods.TryGetValue(DobCloneImporter.GetMethodFullName(m), out var methodDob) &&
                    methodDob == asmData.version)
                {
                    var newMethodImpl = new MemberCloner(
                            newModule,
                            _ => new DobCloneImporter(assembly, new MemberCloneContext(newModule)))
                        .Include(m).Clone().GetClonedMember(m);
                    existingTypeStub.Methods.Add(newMethodImpl);

                    HandleNewMethod(m, newMethodImpl, existingTypeStub, true);

                    continue;
                }

                // Existing method
                var existingMethod = new MemberCloner(
                        newModule,
                        ctx => new DobCloneImporter(assembly, new MemberCloneContext(newModule)))
                    .Include(m).Clone().GetClonedMember(m);

                existingTypeStub.Methods.Add(existingMethod);

                existingMethod.Name += "_Impl";
                existingMethod.Attributes &= ~MethodAttributes.SpecialName;
                existingMethod.Attributes &= ~MethodAttributes.RuntimeSpecialName;
                existingMethod.Attributes &= ~MethodAttributes.Virtual;
                existingMethod.Attributes &= ~MethodAttributes.NewSlot;
                existingMethod.Attributes &= ~MethodAttributes.Final;
                existingMethod.Attributes &= ~MethodAttributes.Abstract;

                existingMethod.Parameters.PullUpdatesFromMethodSignature();

                var thisType = m.Parameters.ThisParameter?.ParameterType;
                if (thisType != null)
                    thisType = new DobCloneImporter(assembly, new MemberCloneContext(newModule)).ImportTypeSignature(thisType);
                AsmHelper.TurnInstanceIntoStatic(existingMethod, thisType);

                var ptrGetter = MakeAndAddPtrGetter(existingMethod, existingTypeStub, newModule, m.DeclaringModule);

                if (m is not { IsConstructor: true, IsStatic: true })
                {
                    ptrGetterToOrig[ptrGetter] = DobCloneImporter.GetMethodFullName(m);
                    implToOldOrigRid[DobCloneImporter.GetMethodFullName(existingMethod)] = m.MetadataToken.Rid;
                }
            }

            var cctor = MakeSpecialNameMethod(".cctor", true);
            var ctor = MakeSpecialNameMethod(".ctor", false);

            // These are handled as existing methods and get the _Impl postfix but must exist for the runtime
            existingTypeStub.Methods.Add(cctor);
            existingTypeStub.Methods.Add(ctor);

            return existingTypeStub;
        }

        TypeDefinition HandleNewType(TypeDefinition type)
        {
            var cloner = new MemberCloner(
                    newModule,
                    _ => new DobCloneImporter(assembly, new MemberCloneContext(newModule)))
                .Include(type, false);

            var cloneResult = cloner.Clone();
            var clonedType = cloneResult.GetClonedMember(type);

            foreach (var m in type.Methods)
            {
                if (m.CilMethodBody == null) continue;
                HandleNewMethod(m, cloneResult.GetClonedMember(m), clonedType, false);
            }


            return clonedType;
        }

        MethodDefinition MakeSpecialNameMethod(string name, bool @static)
        {
            var method = new MethodDefinition(
                name,
                MethodAttributes.Private | MethodAttributes.HideBySig |
                MethodAttributes.RuntimeSpecialName | MethodAttributes.SpecialName | (@static ? MethodAttributes.Static : 0),
                new MethodSignature(
                    CallingConventionAttributes.Default | (!@static ? CallingConventionAttributes.HasThis : 0),
                    ptrsType.DeclaringModule!.CorLibTypeFactory.Void, null)
            )
            {
                CilMethodBody = new CilMethodBody()
                {
                    Instructions = { CilOpCodes.Ret }
                }
            };

            return method;
        }

        void HandleNewMethod(MethodDefinition m,
            MethodDefinition newMethodImpl,
            TypeDefinition toType,
            bool makeStatic)
        {
            newMethodImpl.Name += "_Impl";
            newMethodImpl.Attributes &= ~MethodAttributes.SpecialName;
            newMethodImpl.Attributes &= ~MethodAttributes.RuntimeSpecialName;
            newMethodImpl.Attributes &= ~MethodAttributes.Virtual;
            newMethodImpl.Attributes &= ~MethodAttributes.NewSlot;
            newMethodImpl.Attributes &= ~MethodAttributes.Final;
            newMethodImpl.Attributes &= ~MethodAttributes.Abstract;
            newMethodImpl.CustomAttributes.Clear(); // Don't duplicate attributes of original method
            newMethodImpl.Parameters.PullUpdatesFromMethodSignature();

            var thisType = m.Parameters.ThisParameter?.ParameterType;
            if (thisType != null)
                thisType = new DobCloneImporter(assembly, new MemberCloneContext(newModule)).ImportTypeSignature(thisType);
            AsmHelper.TurnInstanceIntoStatic(newMethodImpl, thisType);

            m.CilMethodBody.Instructions.Clear();
            m.CilMethodBody.ExceptionHandlers.Clear();

            var newMethodOrig = new MemberCloner(
                    newModule,
                    _ => new DobCloneImporter(assembly, new MemberCloneContext(newModule)))
                .Include(m).Clone().GetClonedMember(m);

            toType.Methods.Add(newMethodOrig);
            newMethodOrig.Parameters.PullUpdatesFromMethodSignature();

            if (makeStatic)
            {
                AsmHelper.TurnInstanceIntoStatic(
                    newMethodOrig,
                    thisType
                );
            }

            WriteOrigMethodBody(
                methodIndex++,
                m,
                newMethodOrig.CilMethodBody,
                newMethodImpl,
                maxGenericParameters,
                mainModule,
                newModule,
                ptrsType,
                getMethodPtr,
                stElemAndReturn,
                runtimeTypeHandleType,
                objTypeSig);

            newMethodOrig.CilMethodBody.Instructions.CalculateOffsets();
        }
    }

    private static void WriteAssemblyToStream(AssemblyDefinition assembly, Stream stream)
    {
        assembly.WriteManifest(
            stream,
            new ManagedPEImageBuilder(
                new DotNetDirectoryFactory(
                    // MetadataBuilderFlags.PreserveMethodDefinitionIndices
                ),
                EmptyErrorListener.Instance
            )
        );
    }

    public static byte[] RewritePortablePdb(byte[] pdb, AssemblyDefinition newAsm, Dictionary<string, uint> implToOldOrigRid)
    {
        var file = MetadataDirectory.FromBytes(pdb);

        var mdi = file.GetStream<TablesStream>().GetTable<MethodDebugInformationRow>();
        var oldRows = mdi.ToArray();
        mdi.Clear();

        var newAsmMethodCount = newAsm.ManifestModule.DotNetDirectory.Metadata.
            GetStream<TablesStream>().
            GetTable<MethodDefinitionRow>().
            Count;

        for (var i = 0; i < newAsmMethodCount; i++)
            mdi.Add(new MethodDebugInformationRow());

        foreach (var t in newAsm.ManifestModule.GetAllTypes())
        {
            foreach (var m in t.Methods)
            {
                if (implToOldOrigRid.ContainsKey(DobCloneImporter.GetMethodFullName(m)))
                    mdi.SetByRid(m.MetadataToken.Rid, oldRows[implToOldOrigRid[DobCloneImporter.GetMethodFullName(m)] - 1]);
            }
        }

        return file.WriteIntoArray();
    }
}
