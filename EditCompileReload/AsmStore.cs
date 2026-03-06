using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using AsmResolver.DotNet;

namespace EditCompileReload;

public class AssemblyData
{
    public int version;
    public Dictionary<string, (int, bool)> types = new(); // Type ToString => version, isValueType
    public Dictionary<string, int> methods = new();
    public Dictionary<string, int> fields = new();
    public List<Assembly> assemblies = new();

    public AssemblyData Copy()
    {
        return new AssemblyData
        {
            version = version,
            types = types.ToDictionary(kv => kv.Key, kv => kv.Value),
            methods = methods.ToDictionary(kv => kv.Key, kv => kv.Value),
            fields = fields.ToDictionary(kv => kv.Key, kv => kv.Value),
            assemblies = assemblies.ToList()
        };
    }
}

public class AsmStore
{
    public static Dictionary<string, AssemblyData> assemblyData = new();
    public static Dictionary<string, (Assembly, int)> methodSigToPtrGetter = new();
    public static HashSet<RuntimeTypeHandle> seenPtrsTypes = new();

    private static readonly BindingFlags allDeclared =
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.GetField |
        BindingFlags.SetField |
        BindingFlags.GetProperty |
        BindingFlags.SetProperty |
        BindingFlags.DeclaredOnly;

    public static IntPtr GetMethodPtr(
        string sig,
        RuntimeMethodHandle ptrGetterMethod,
        RuntimeTypeHandle ptrGetterDeclaringType,
        RuntimeTypeHandle ptrsType,
        RuntimeTypeHandle[]? genericArgsFromType,
        RuntimeTypeHandle[]? genericArgsFromMethod
    )
    {
        lock (seenPtrsTypes)
        {
            seenPtrsTypes.Add(ptrsType);
        }

        var method = methodSigToPtrGetter.TryGetValue(sig, out var asmToken) ?
            asmToken.Item1.ManifestModule.ResolveMethod(asmToken.Item2) :
            MethodBase.GetMethodFromHandle(ptrGetterMethod, ptrGetterDeclaringType);
        int token = method.MetadataToken;

        // Common case
        if (genericArgsFromType == null && genericArgsFromMethod == null)
            return (IntPtr)method.Invoke(null, null);

        if (method.DeclaringType is { IsGenericTypeDefinition: true })
        {
            // First apply arguments to type, then to method found on the type
            var declaringType = genericArgsFromType != null ?
                method.DeclaringType.MakeGenericType(genericArgsFromType.Select(Type.GetTypeFromHandle).ToArray()) :
                method.DeclaringType;
            method = declaringType.GetMethods(allDeclared).First(m => m.MetadataToken == token);
        }

        if (method is MethodInfo { IsGenericMethodDefinition: true } info && genericArgsFromMethod != null)
            method = info.MakeGenericMethod(genericArgsFromMethod.Select(Type.GetTypeFromHandle).ToArray());

        return (IntPtr)method.Invoke(null, null);
    }

    public static void UpdateMethodReferences(AssemblyDefinition asm, Assembly asmReflection, Dictionary<string, string> ptrGetterToOrig)
    {
        foreach (var t in asm.ManifestModule.GetAllTypes())
        {
            foreach (var m in t.Methods)
            {
                if (ptrGetterToOrig.ContainsKey(DobCloneImporter.GetMethodFullName(m)))
                    methodSigToPtrGetter[ptrGetterToOrig[DobCloneImporter.GetMethodFullName(m)]] = (asmReflection, m.MetadataToken.ToInt32());
            }
        }
    }

    public static void Clear()
    {
        assemblyData.Clear();
        methodSigToPtrGetter.Clear();
        seenPtrsTypes.Clear();
    }
}
