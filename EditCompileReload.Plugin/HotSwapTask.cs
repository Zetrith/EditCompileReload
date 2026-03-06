using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace EditCompileReload.Plugin;

internal class HotSwapTask : Task
{
    public string ProjectAssembly { get; set; }
    public string OutputFolder { get; set; }

    [Output]
    public ITaskItem[] MoveSourceFiles { get; set; }

    [Output]
    public ITaskItem[] DestinationFiles { get; set; }

    public override bool Execute()
    {
        Directory.CreateDirectory(OutputFolder);
        string outputFile = $"{OutputFolder}{Path.GetFileName(ProjectAssembly)}";
        (byte[] asmBytes, _) = AsmWriter.RewriteOriginal(ProjectAssembly);
        File.WriteAllBytes(outputFile, asmBytes);
        MoveSourceFiles = [new TaskItem(outputFile)];
        DestinationFiles = [new TaskItem(ProjectAssembly)];
        return true;
    }
}
