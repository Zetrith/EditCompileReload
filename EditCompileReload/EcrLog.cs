using System;

namespace EditCompileReload;

public static class EcrLog
{
    public static Action<string>? messageCallback = Console.WriteLine;
    public static Action<string>? errorCallback = Console.WriteLine;
    public static Action<string>? verboseCallback;

    public static void Message(string message)
    {
        messageCallback?.Invoke(message);
    }

    public static void Error(string message)
    {
        errorCallback?.Invoke(message);
    }

    public static void Verbose(string message)
    {
        verboseCallback?.Invoke(message);
    }
}
