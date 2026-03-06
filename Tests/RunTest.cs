using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE;
using EditCompileReload;

namespace Tests;

public class RunTest
{
    private AssemblyDefinition? origAsm;
    private Assembly? origAsmReflection;

    private void Reset()
    {
        origAsm = null;
        origAsmReflection = null;
        AsmStore.Clear();
    }

    private void LoadOriginalAssembly()
    {
        // Prevent from being loaded as a dependency
        File.Delete("TestAssembly1.dll");

        origAsm = AssemblyDefinition.FromImage(
            PEImage.FromFile(@"..\..\..\..\TestAssembly1\bin\Debug\net472\TestAssembly1.dll"));
        AsmWriter.UpdateMemberDobs(origAsm);

        var (origAsmBytes, implToOldOrigRid) = AsmWriter.RewriteOriginal(
            @"..\..\..\..\TestAssembly1\bin\Debug\net472\TestAssembly1.dll");

        var pdb = AsmWriter.RewritePortablePdb(
            File.ReadAllBytes(@"..\..\..\..\TestAssembly1\bin\Debug\net472\TestAssembly1.pdb"),
            AssemblyDefinition.FromBytes(origAsmBytes),
            implToOldOrigRid
        );

        origAsmReflection = Assembly.Load(origAsmBytes, pdb);
        File.WriteAllBytes("orig.dll", origAsmBytes);
        File.WriteAllBytes("orig.pdb", pdb);

        AppDomain.CurrentDomain.AssemblyResolve +=
            (_, args) => origAsm != null && args.Name.StartsWith(origAsm.Name!) ? origAsmReflection : null;

        AsmStore.assemblyData[origAsm.Name!].assemblies.Add(origAsmReflection);
        AsmStore.assemblyData[origAsm.Name!].version++;
    }

    private void LoadChangedAssembly()
    {
        var asm2 = AssemblyDefinition.FromImage(
            PEImage.FromFile(@"..\..\..\..\TestAssembly2\bin\Debug\net472\TestAssembly1.dll"));
        AsmWriter.UpdateMemberDobs(asm2);

        var (newAsm2Bytes, ptrGetterToOrig, implToOldOrigRid) =
            AsmWriter.RewriteChanged(asm2);
        AsmStore.assemblyData[asm2.Name!].version++;

        var newAsm2 = AssemblyDefinition.FromBytes(newAsm2Bytes);

        var pdb = AsmWriter.RewritePortablePdb(
            File.ReadAllBytes(@"..\..\..\..\TestAssembly2\bin\Debug\net472\TestAssembly1.pdb"),
            newAsm2,
            implToOldOrigRid
        );

        var newAsm2Reflection = AppDomain.CurrentDomain.Load(newAsm2Bytes, pdb);

        File.WriteAllBytes("changed.dll", newAsm2Bytes);
        File.WriteAllBytes("changed.pdb", pdb);

        AsmHelper.CheckIsUsable(newAsm2Reflection);

        AsmStore.UpdateMethodReferences(newAsm2, newAsm2Reflection, ptrGetterToOrig);
        Ecr.ResetAllFields();
    }

    [Test]
    public void TestBasics()
    {
        LoadOriginalAssembly();

        Assert.That(
            origAsmReflection.GetType("TestAssembly1.Class1").GetMethod("TestBasics").Invoke(null, null),
            Is.EqualTo([
                (1, "Existing static field"),
                (1, "Existing constructor"),
                (1, "Existing instance method"),
                (0, "Existing property auto-getter"),
                (1, "Existing static method on nested type"),
                (1, "Existing instance method on struct type"),
                (0, "Existing property auto-getter on struct type"),
            ])
        );

        LoadChangedAssembly();

        Assert.That(
            origAsmReflection.GetType("TestAssembly1.Class1").GetMethod("TestBasics").Invoke(null, null),
            Is.EqualTo([
                (1, "Existing static field"),
                (0, "New static field (they currently take on the default value, 0)"),
                (0, "New static field"),
                (2, "New field on new type"),
                (2, "Existing constructor"),
                (2, "Existing instance method"),
                (2, "New instance method"),
                (0, "Existing property auto-getter"),
                (2, "Existing static method on nested type"),
                (2, "New static method on nested type"),
                (2, "New static method on by-ref type"),
                (2, "Existing instance method on struct type"),
                (0, "Existing property auto-getter on struct type"),
            ])
        );
    }

    [Test]
    public void TestGenerics()
    {
        Reset();
        LoadOriginalAssembly();

        Assert.That(
            origAsmReflection.GetType("TestAssembly1.Class1").GetMethod("TestGenerics").Invoke(null, null),
            Is.EqualTo([
                (1, "Existing static field on generic type"),
                (1, "Existing static generic method"),
                (1, "Existing static method on generic type"),
                (1, "Existing static generic method on generic type"),
                (1, "Existing static method on (nested type on generic type)"),
                (1, "Existing instance generic method"),
                (1, "Existing instance method on generic type"),
                (1, "Existing instance generic method on generic type"),
                (0, "Test existing instance generic method with by-ref parameter"),
                (0, "Existing property auto-getter on generic struct type"),
            ])
        );

        LoadChangedAssembly();

        Assert.That(
            origAsmReflection.GetType("TestAssembly1.Class1").GetMethod("TestGenerics").Invoke(null, null),
            Is.EqualTo([
                (1, "Existing static field on generic type"),
                (2, "Existing static generic method"),
                (2, "Existing static method on generic type"),
                (2, "Existing static generic method on generic type"),
                (2, "Existing static method on (nested type on generic type)"),
                (2, "Existing instance generic method"),
                (2, "Existing instance method on generic type"),
                (2, "Existing instance generic method on generic type"),
                (0, "Existing property auto-getter on generic struct type"),
                ((object)new List<string> { "" }, "Existing static generic method with lambda"),
                (2, "New static field on generic type"),
                (2, "New static generic method"),
                (2, "New static method on generic type"),
                (2, "New static generic method on generic type"),
                (2, "New static method on (nested type on generic type)"),
                (2, "New instance generic method"),
                (2, "New instance method on generic type"),
                (2, "New instance generic method on generic type"),
            ])
        );
    }
}
