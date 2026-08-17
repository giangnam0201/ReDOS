using System.Runtime.InteropServices;

namespace ReDOS;

/// <summary>
/// Shapes the host console to the DOS screen before a program starts.
///
/// This is not cosmetic. The console core mirrors the 80x25 text buffer into whatever console it is
/// given; when that console is wider or taller, the program's screen clears only cover the area DOS
/// believes exists and everything outside it stays on screen, so frames pile up on top of each other.
/// </summary>
internal static partial class ConsoleLayout
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, Coord dwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleWindowInfo(IntPtr hConsoleOutput, [MarshalAs(UnmanagedType.Bool)] bool bAbsolute, in SmallRect lpConsoleWindow);

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public short Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "FillConsoleOutputCharacterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // The character is passed as a UTF-16 code unit; char itself needs marshalling config here.
    private static partial bool FillConsoleOutputCharacter(IntPtr hConsoleOutput, ushort cCharacter, uint nLength, Coord dwWriteCoord, out uint lpNumberOfCharsWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FillConsoleOutputAttribute(IntPtr hConsoleOutput, short wAttribute, uint nLength, Coord dwWriteCoord, out uint lpNumberOfAttributesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCursorPosition(IntPtr hConsoleOutput, Coord dwCursorPosition);

    /// <summary>Give the console a DOS-shaped screen and wipe anything already on it.</summary>
    internal static void ApplyDosScreen(int columns = 80, int rows = 25)
    {
        EnableVirtualTerminal();

        // Windows Terminal hosts programs through a pseudoconsole, which ignores the classic
        // resize APIs; the terminal itself honours the XTerm window-resize sequence instead.
        Write($"[8;{rows};{columns}t");

        TryResizeClassicConsole(columns, rows);

        // Erase scrollback as well as the visible screen, so nothing can scroll back into view.
        Write("[3J[H[2J");

        // Escape sequences only reach a terminal that interprets them; wipe the buffer directly too.
        ClearBuffer();

        AppPaths.Log($"console shaped for DOS: {Describe()}");
    }

    /// <summary>The console's real dimensions, for the log — the only way to see them from outside.</summary>
    internal static string Describe()
    {
        IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == InvalidHandle) return "no console";
        if (!GetConsoleScreenBufferInfo(handle, out var info)) return "console size unavailable";

        int windowColumns = info.Window.Right - info.Window.Left + 1;
        int windowRows = info.Window.Bottom - info.Window.Top + 1;
        return $"window {windowColumns}x{windowRows}, buffer {info.Size.X}x{info.Size.Y}";
    }

    /// <summary>Blank the whole screen buffer, including anything a previous program left behind.</summary>
    private static void ClearBuffer()
    {
        IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == InvalidHandle) return;
        if (!GetConsoleScreenBufferInfo(handle, out var info)) return;

        var origin = new Coord { X = 0, Y = 0 };
        uint cells = (uint)(info.Size.X * info.Size.Y);

        FillConsoleOutputCharacter(handle, (ushort)' ', cells, origin, out _);
        FillConsoleOutputAttribute(handle, info.Attributes, cells, origin, out _);
        SetConsoleCursorPosition(handle, origin);
    }

    private static void EnableVirtualTerminal()
    {
        IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == InvalidHandle) return;

        if (GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    /// <summary>
    /// Resize a real console window. The window has to shrink before the buffer can, and grow only
    /// after it — a buffer smaller than its window is rejected.
    /// </summary>
    private static void TryResizeClassicConsole(int columns, int rows)
    {
        IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == InvalidHandle) return;

        try
        {
            var minimal = new SmallRect { Left = 0, Top = 0, Right = 0, Bottom = 0 };
            SetConsoleWindowInfo(handle, true, in minimal);

            // A buffer exactly the size of the window means no scrollback for old frames to hide in.
            SetConsoleScreenBufferSize(handle, new Coord { X = (short)columns, Y = (short)rows });

            var target = new SmallRect
            {
                Left = 0,
                Top = 0,
                Right = (short)(columns - 1),
                Bottom = (short)(rows - 1),
            };
            SetConsoleWindowInfo(handle, true, in target);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
        {
            AppPaths.Log($"could not resize the console: {ex.Message}");
        }
    }

    private static void Write(string sequence)
    {
        try { Console.Out.Write(sequence); Console.Out.Flush(); }
        catch (IOException) { /* no console to shape */ }
    }
}
