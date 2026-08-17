using System.Runtime.InteropServices;

namespace ReDOS;

/// <summary>Thin P/Invoke layer. Kept small on purpose: ReDOS ships as a single self-contained exe.</summary>
internal static partial class Native
{
    internal const int MB_OK = 0x0;
    internal const int MB_YESNO = 0x4;
    internal const int MB_ICONERROR = 0x10;
    internal const int MB_ICONQUESTION = 0x20;
    internal const int MB_ICONINFO = 0x40;
    internal const int IDYES = 6;

    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(IntPtr hWnd, string text, string caption, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetShortPathName(string lpszLongPath, char[]? lpszShortPath, uint cchBuffer);

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// Tell already-running programs that the environment changed, so a shell opened afterwards sees
    /// the new PATH without a sign-out. Explorer relays it to the shells it starts.
    /// </summary>
    internal static void BroadcastEnvironmentChange() =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 5000, out _);

    /// <summary>Ask the filesystem for the 8.3 alias of a path, so DOS never sees a long name it cannot type.</summary>
    internal static string? TryGetShortPath(string longPath)
    {
        var buffer = new char[520];
        uint len = GetShortPathName(longPath, buffer, (uint)buffer.Length);
        if (len == 0 || len >= buffer.Length) return null;
        return new string(buffer, 0, (int)len);
    }

    internal static void RefreshShellAssociations() =>
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
}
