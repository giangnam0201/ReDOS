namespace ReDOS;

/// <summary>
/// ReDOS is a GUI subsystem app so double-clicking never flashes a console, but it is also a
/// usable CLI. This routes messages to whichever one the user is actually looking at — and to
/// neither, silently, when nothing is listening at all.
/// </summary>
internal sealed class Reporter
{
    private readonly bool _text;
    private readonly bool _canPrompt;

    internal Reporter(bool console)
    {
        _text = console || ConsoleHost.NonInteractive;

        // A message box with nobody to click it hangs forever, so only offer one when a human
        // could plausibly be looking at the screen.
        _canPrompt = !console && !ConsoleHost.NonInteractive;
    }

    internal bool HasConsole => _text;

    internal void Info(string message) => Emit(message, Native.MB_ICONINFO, error: false);

    internal void Error(string message)
    {
        AppPaths.Log("error: " + message.Replace("\n", " "));
        Emit(message, Native.MB_ICONERROR, error: true);
    }

    /// <summary>Progress text. In GUI mode this is intentionally silent — seamless means no popups.</summary>
    internal void Status(string message)
    {
        AppPaths.Log(message);
        if (_text) Write(message, error: false);
    }

    internal bool Confirm(string message)
    {
        if (_canPrompt)
            return Native.MessageBox(IntPtr.Zero, message, "ReDOS", Native.MB_YESNO | Native.MB_ICONQUESTION) == Native.IDYES;

        if (!_text)
        {
            AppPaths.Log($"declined (nothing to prompt with): {message.Replace("\n", " ")}");
            return false;
        }

        Write(message + " [y/N] ", error: false, newLine: false);
        string? answer = TryReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private void Emit(string message, int icon, bool error)
    {
        if (_text || !_canPrompt) Write(error ? "ReDOS: " + message : message, error);
        else Native.MessageBox(IntPtr.Zero, message, "ReDOS", Native.MB_OK | icon);
    }

    private static void Write(string message, bool error, bool newLine = true)
    {
        try
        {
            var writer = error ? Console.Error : Console.Out;
            if (newLine) writer.WriteLine(message);
            else writer.Write(message);
        }
        catch (IOException)
        {
            // No console and no pipe: the log is the only record, and it already has this.
        }
    }

    private static string? TryReadLine()
    {
        try { return Console.ReadLine(); }
        catch (IOException) { return null; }
    }
}
