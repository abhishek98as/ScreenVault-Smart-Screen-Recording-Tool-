using System.Runtime.InteropServices;

namespace ScreenVault.App.Platform;

public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    public static bool TryAttachToParentConsole()
    {
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, Console.OutputEncoding) { AutoFlush = true });
                return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    public static void Detach()
    {
        FreeConsole();
    }
}
