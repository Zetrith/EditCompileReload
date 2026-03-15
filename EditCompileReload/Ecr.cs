using System.Reflection;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE;

namespace EditCompileReload;

public class Ecr
{
    public static void ResetAllFields()
    {
        lock (AsmStore.seenPtrsTypes)
        {
            EcrLog.Message($"Resetting {AsmStore.seenPtrsTypes.Count} seen generic type configurations");

            foreach (var ptrsType in AsmStore.seenPtrsTypes)
            {
                var arrField = Type.GetTypeFromHandle(ptrsType).GetField("arr");
                arrField.SetValue(null, new IntPtr[((IntPtr[])arrField.GetValue(null)).Length]);
            }
        }
    }

    private static Thread processingThread = new(ProcessingThreadLogic);
    private static BlockingQueue<Action> actionQueue = new();

    static Ecr()
    {
        processingThread.Name = "EditCompileReload processing thread";
        processingThread.Start();
    }

    public static void ProcessingThreadLogic()
    {
        while (true)
        {
            var action = actionQueue.Dequeue();
            action();
        }
    }

    private static HashSet<string> registeredFileWatchers = [];

    public static void RegisterFileWatcher(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        if (!registeredFileWatchers.Add(filePath))
            return;

        var fileName = Path.GetFileName(filePath);
        var watcher = new FileSystemWatcher();
        watcher.Path = Path.GetDirectoryName(filePath);
        watcher.NotifyFilter = NotifyFilters.LastWrite;
        watcher.Filter = fileName;

        var origAsm = AssemblyDefinition.FromImage(PEImage.FromFile(filePath));
        string origAsmName = origAsm.Name!;
        var origAsmReflection = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == origAsmName);

        AsmWriter.UpdateMemberDobs(origAsm);
        AsmStore.assemblyData[origAsmName].assemblies.Add(origAsmReflection);
        AsmStore.assemblyData[origAsmName].version++;

        EcrLog.Message("Registered file watcher");

        watcher.Changed += (_, e) =>
        {
            EcrLog.Verbose($"Got change event: {e.FullPath} {e.Name} {fileName}");

            if (!e.FullPath.EndsWith(fileName)) return;

            var version = AsmStore.assemblyData[origAsmName].version;
            actionQueue.Enqueue(() =>
            {
                if (AsmStore.assemblyData[origAsmName].version != version)
                    return; // Another request has already processed this assembly for now
                ProcessAssembly(origAsmName, e.FullPath);
            });
        };

        watcher.EnableRaisingEvents = true;
    }

    public static bool writeSwappedAssembly;

    private static void ProcessAssembly(string assemblyName, string path)
    {
        EcrLog.Message("ProcessAssembly start");

        var assemblyDataCopy = AsmStore.assemblyData[assemblyName].Copy();

        try
        {
            var assembly =
                AssemblyDefinition.FromImage(
                    PEImage.FromFile(path),
                    new ModuleReaderParameters(Path.GetDirectoryName(path), EmptyErrorListener.Instance));

            AsmWriter.UpdateMemberDobs(assembly);
            var (newAsmBytes, ptrGetterToOrig, _) = AsmWriter.RewriteChanged(assembly);

            if (writeSwappedAssembly)
                File.WriteAllBytes("swapped.dll", newAsmBytes);

            var newAsm = AssemblyDefinition.FromBytes(newAsmBytes);
            var newAsmReflection = AppDomain.CurrentDomain.Load(newAsmBytes);

            AsmHelper.CheckIsUsable(newAsmReflection);
            AsmStore.UpdateMethodReferences(newAsm, newAsmReflection, ptrGetterToOrig);

            AsmStore.assemblyData[assemblyName].version++;

            ResetAllFields();
            AsmStore.assemblyData[assemblyName].assemblies.Add(newAsmReflection);

            foreach (var t in newAsmReflection.GetTypes())
            {
                try
                {
                    t.GetMethod(
                        "OnEditCompileReload_Impl",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                        null,
                        [], null
                    )?.Invoke(null, null);
                }
                catch (Exception e)
                {
                    EcrLog.Error($"Exception while running reload callback on {t}: {e}");
                }
            }
        }
        catch (Exception e)
        {
            AsmStore.assemblyData[assemblyName] = assemblyDataCopy;
            EcrLog.Error($"ProcessAssembly exception {e}");
        }
    }
}
