using System;
using System.Runtime.InteropServices;

namespace Host;

internal static class DebugConsole
{
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static bool IsOpen => GetConsoleWindow() != IntPtr.Zero;

    public static void Open()
    {
        if (IsOpen)
        {
            return;
        }

        if (AllocConsole())
        {
            Console.Title = "Host Debug";
        }
    }

    public static void WriteLine(string message)
    {
        if (!IsOpen)
        {
            return;
        }

        Console.WriteLine(message);
    }
}
