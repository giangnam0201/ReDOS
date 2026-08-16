using System.Diagnostics;

namespace ReDOS;

/// <summary>
/// Opens the DOS machine in a terminal window. Windows Terminal is preferred when it is installed
/// (it is the default on Windows 11 and a normal install on Windows 10); otherwise ReDOS falls back
/// to a classic console window, which every Windows 10 machine has.
/// </summary>
internal static class Terminal
{
    /// <summary>True when a console-hosted DOS session is actually possible on this machine.</summary>
    internal static bool CanHostDosSession => ConsoleCore.IsAvailable;

    internal static string? FindWindowsTerminal()
    {
        // wt.exe normally lives on PATH via the WindowsApps alias.
        string? onPath = SearchPath("wt.exe");
        if (onPath is not null) return onPath;

        string localApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");

        return File.Exists(localApps) ? localApps : null;
    }

    /// <summary>
    /// Launch a new terminal window running <c>ReDOS session</c>, which takes over that console and
    /// turns it into the DOS machine.
    /// </summary>
    internal static int OpenDosSession(string? programPath, IReadOnlyList<string> programArgs, Reporter report)
    {
        Sandbox.Ensure();

        var innerArgs = new List<string> { "session" };
        if (programPath is not null)
        {
            innerArgs.Add(programPath);
            innerArgs.AddRange(programArgs);
        }

        string? wt = FindWindowsTerminal();
        ProcessStartInfo psi;

        if (wt is not null)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(programPath is null ? "MS-DOS" : Path.GetFileNameWithoutExtension(programPath));
            psi.ArgumentList.Add(AppPaths.ExecutablePath);
            foreach (string arg in innerArgs) psi.ArgumentList.Add(arg);
        }
        else
        {
            // "start" gives the child its own console window; cmd is only the launcher, not the host.
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(programPath is null ? "MS-DOS" : Path.GetFileNameWithoutExtension(programPath));
            psi.ArgumentList.Add(AppPaths.ExecutablePath);
            foreach (string arg in innerArgs) psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            return process is null ? 1 : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            report.Error($"Could not open a terminal window: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Run the DOS session inside the console this process already owns. This is what the terminal
    /// window launched above ends up executing.
    /// </summary>
    internal static int RunInThisConsole(string? programPath, IReadOnlyList<string> programArgs, Reporter report)
    {
        Sandbox.Ensure();

        var command = ConsoleCore.BuildCommand(programPath, programArgs);
        if (command is null)
        {
            report.Error(
                "No console DOS core is installed, so ReDOS cannot turn this terminal into a DOS machine.\n" +
                "Install one with:  ReDOS console-core --install <path to msdos-player.exe or its zip>\n" +
                "Opening the graphical DOS machine instead.");

            return Launcher.RunPrompt(report);
        }

        var psi = new ProcessStartInfo(command.Value.Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = programPath is not null
                ? Path.GetDirectoryName(programPath)!
                : Sandbox.Root,
        };
        foreach (string arg in command.Value.Args) psi.ArgumentList.Add(arg);

        // The sandbox is drive C: here too, so a console session sees exactly the same files.
        psi.Environment["TEMP"] = Sandbox.TempDir;
        psi.Environment["TMP"] = Sandbox.TempDir;

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            report.Error($"The console DOS core failed to start: {ex.Message}");
            return 3;
        }
    }

    private static string? SearchPath(string fileName)
    {
        string? paths = Environment.GetEnvironmentVariable("PATH");
        if (paths is null) return null;

        foreach (string directory in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }
}
