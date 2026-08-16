using System.Diagnostics;

namespace ReDOS;

/// <summary>
/// Runs DOS inside a real terminal. Windows Terminal is preferred when it is installed (the default
/// on Windows 11, a normal install on Windows 10); otherwise a classic console window is used, which
/// every Windows 10 machine has.
/// </summary>
internal static class Terminal
{
    /// <summary>True when a console-hosted DOS session is possible without downloading anything.</summary>
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
    /// Open a terminal window running <c>ReDOS session</c>, which takes over that console and turns
    /// it into the DOS machine.
    /// </summary>
    internal static int OpenDosSession(string? programPath, IReadOnlyList<string> programArgs, Reporter report)
    {
        Sandbox.Ensure();

        string title = programPath is null ? "MS-DOS" : Path.GetFileNameWithoutExtension(programPath);

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

            // Window-level options must come before the subcommand: after "new-tab" they are parsed
            // as part of the command line to run, not as options.
            //   -w -1     force a new window instead of a tab in whatever window has focus
            //   --size    DOS text mode is 80x25, so nothing wraps or leaves dead space
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("-1");
            psi.ArgumentList.Add("--size");
            psi.ArgumentList.Add("80,25");

            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(AppPaths.ExecutablePath);
            foreach (string arg in innerArgs) psi.ArgumentList.Add(arg);
        }
        else
        {
            // "start" gives the child its own console window; cmd is only the launcher, not the host.
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(title);
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
    /// Become the DOS machine inside the console this process already owns — this is what the
    /// terminal window opened above ends up executing.
    /// </summary>
    internal static int RunInThisConsole(string? programPath, IReadOnlyList<string> programArgs, Reporter report)
    {
        Sandbox.Ensure();

        string core;
        try
        {
            core = ConsoleCore.EnsureAsync(report.Status).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report.Error(
                $"The console DOS core could not be obtained ({ex.Message}).\n" +
                "Opening the graphical DOS machine instead.");
            return Launcher.RunPrompt(report);
        }

        // Without a program there is nothing for the console core to run: unlike the graphical core
        // it has no built-in shell, so it needs a real COMMAND.COM.
        if (programPath is null)
        {
            programPath = FindDosShell();
            if (programPath is null)
            {
                report.Error(
                    "The console DOS machine needs a DOS shell to sit at, and no COMMAND.COM was found in\n" +
                    $"{Sandbox.DosDir}. Copy one there for a terminal prompt.\n" +
                    "Opening the graphical DOS machine instead.");
                return Launcher.RunPrompt(report);
            }
        }

        // Give DOS a clean drive root: the sandbox becomes a drive letter of its own, so programs
        // see PROGRAMS\FOO at the root instead of a long Windows path.
        using var drive = SubstDrive.Create(Sandbox.Root);

        string workingDirectory = Path.GetDirectoryName(programPath) ?? Sandbox.Root;
        string program = programPath;

        if (drive is not null && Sandbox.Contains(programPath))
        {
            workingDirectory = drive.Translate(workingDirectory);
            program = Path.GetFileName(programPath);
        }

        var psi = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add(program);
        foreach (string arg in programArgs) psi.ArgumentList.Add(arg);

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

    /// <summary>A COMMAND.COM the user has dropped into the sandbox, if there is one.</summary>
    private static string? FindDosShell()
    {
        foreach (string directory in new[] { Sandbox.DosDir, Sandbox.Root })
        {
            string candidate = Path.Combine(directory, "COMMAND.COM");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
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

/// <summary>
/// A temporary drive letter mapped to a folder, so DOS sees a drive root rather than a long path.
/// Removed again when the session ends.
/// </summary>
internal sealed class SubstDrive : IDisposable
{
    private readonly string _target;

    internal string Letter { get; }

    private SubstDrive(string letter, string target)
    {
        Letter = letter;
        _target = target;
    }

    /// <summary>Map <paramref name="folder"/> to a free drive letter, or null if none could be taken.</summary>
    internal static SubstDrive? Create(string folder)
    {
        string? letter = Environment.GetEnvironmentVariable("REDOS_DRIVE")?.TrimEnd(':', '\\');
        var candidates = letter is { Length: 1 }
            ? [letter.ToUpperInvariant()]
            : new[] { "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y" };

        var taken = DriveInfo.GetDrives()
            .Select(d => d.Name[..1].ToUpperInvariant())
            .ToHashSet();

        foreach (string candidate in candidates)
        {
            if (taken.Contains(candidate)) continue;
            if (!RunSubst($"{candidate}:", folder)) continue;
            return new SubstDrive(candidate, Path.GetFullPath(folder));
        }

        AppPaths.Log("no free drive letter for the sandbox; using full paths instead");
        return null;
    }

    /// <summary>Rewrite a path inside the mapped folder to its drive-letter equivalent.</summary>
    internal string Translate(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(_target, StringComparison.OrdinalIgnoreCase)) return full;

        string relative = full[_target.Length..].TrimStart(Path.DirectorySeparatorChar);
        return $"{Letter}:\\{relative}";
    }

    public void Dispose()
    {
        try { RunSubst($"{Letter}:", null); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppPaths.Log($"could not release drive {Letter}: {ex.Message}");
        }
    }

    private static bool RunSubst(string drive, string? folder)
    {
        var psi = new ProcessStartInfo("subst")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add(drive);
        if (folder is null) psi.ArgumentList.Add("/D");
        else psi.ArgumentList.Add(folder);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
