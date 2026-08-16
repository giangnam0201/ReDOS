using System.Runtime.InteropServices;
using System.Text;

namespace ReDOS;

/// <summary>
/// ReDOS is a GUI-subsystem program so double-clicking it never flashes a console window, but it is
/// also a real command-line tool. This works out whether anyone is listening on stdout and wires the
/// streams up accordingly: an inherited pipe (redirection) wins, otherwise the parent's console.
/// </summary>
internal static partial class ConsoleHost
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>
    /// Set when ReDOS must never open a window that waits for a click — scripts, scheduled tasks and
    /// CI, where a modal dialog would simply hang forever.
    /// </summary>
    internal static bool NonInteractive =>
        Environment.GetEnvironmentVariable("REDOS_NO_UI") is { Length: > 0 } value
        && value is not "0" and not "false";

    /// <summary>Connect to whatever is listening. Returns false when nothing is — GUI mode.</summary>
    internal static bool Attach()
    {
        IntPtr stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        bool inherited = stdout != IntPtr.Zero && stdout != InvalidHandle;

        bool attached = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        // Redirection to a file or pipe already gave us usable streams; leave them alone.
        if (inherited) return true;
        if (!attached) return false;

        // Attaching to the parent console does not rebind the runtime's streams, so do it by hand.
        return TryBindToConsole();
    }

    private static bool TryBindToConsole()
    {
        try
        {
            IntPtr output = CreateFile("CONOUT$", GENERIC_WRITE | GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (output == InvalidHandle) return false;

            var writer = new StreamWriter(
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(output, ownsHandle: true),
                    FileAccess.Write),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            Console.SetOut(writer);
            Console.SetError(writer);

            IntPtr input = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (input != InvalidHandle)
            {
                Console.SetIn(new StreamReader(
                    new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(input, ownsHandle: true),
                        FileAccess.Read)));
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// A GUI-subsystem process returns control to the shell the moment it starts, so its output lands
    /// after the next prompt has been drawn. Printing a fresh prompt-like break keeps that readable.
    /// </summary>
    internal static void FinishOutput()
    {
        try { Console.Out.Flush(); }
        catch (IOException) { /* the listener went away; nothing to do */ }
    }
}
