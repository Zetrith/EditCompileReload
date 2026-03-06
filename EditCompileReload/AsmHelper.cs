using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace EditCompileReload;

public static class AsmHelper
{
    internal static void TurnInstanceIntoStatic(MethodDefinition clone, TypeSignature? thisType)
    {
        if (!clone.IsStatic)
        {
            clone.ParameterDefinitions.Insert(0, new ParameterDefinition("this")
            {
                Sequence = 1
            });

            clone.Signature.ParameterTypes.Insert(
                0,
                thisType!
            );
        }

        clone.Signature.HasThis = false;

        clone.Parameters.PullUpdatesFromMethodSignature();

        clone.IsVirtual = false;
        clone.IsStatic = true;
    }

    public static void CheckIsUsable(Assembly asm)
    {
        try
        {
            asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            EcrLog.Message($"Exception getting types of new assembly: {ToStringSafeEnumerable(e.Types)}\n{e}");
        }
    }

    private static string ToStringSafeEnumerable(IEnumerable enumerable)
    {
        if (enumerable == null)
        {
            return "null";
        }
        try
        {
            string text = "";
            foreach (object item in enumerable)
            {
                if (text.Length > 0)
                {
                    text += ", ";
                }
                text += item?.ToString();
            }
            return text;
        }
        catch (Exception ex)
        {
            return "error";
        }
    }

    internal static long GetMillisAndRestart(this Stopwatch stopwatch)
    {
        long value = stopwatch.ElapsedMilliseconds;
        stopwatch.Reset();
        stopwatch.Start();
        return value;
    }

    internal static string SafeToString(this IMetadataMember? self)
    {
        if (self is null)
            return "null";

        try
        {
            string value = self.ToString()!;
            if (value.Length > 200)
                value = $"{value.Remove(197)}... (truncated)";
            if (self.MetadataToken.Rid != 0)
                value = $"{value} (0x{self.MetadataToken.ToString()})";
            return value;
        }
        catch
        {
            return $"0x{self.MetadataToken.ToString()}";
        }
    }
}
