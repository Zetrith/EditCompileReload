using AsmResolver.DotNet;

namespace EditCompileReload;

internal class CorLibReplaceImporter(ModuleDefinition module) : ReferenceImporter(module)
{
    protected override AssemblyReference ImportAssembly(AssemblyDescriptor assembly)
    {
        if (assembly.IsCorLib && TargetModule.CorLibTypeFactory.CorLibScope is AssemblyReference asm)
            return asm;
        return base.ImportAssembly(assembly);
    }
}
