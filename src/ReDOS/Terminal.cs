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

        // Every window ReDOS opens is marked, so a DOS session is never mistaken for a normal shell.
        string title = programPath is null
            ? $"(ReDOS) {SubstDrive.PlannedLetter}:\\"
            : $"(ReDOS) {Path.GetFileNameWithoutExtension(programPath)}";

        var innerArgs = new List<string> { "session" };
        if (programPath is not null)
        {
            innerArgs.Add(programPath);
            innerArgs.AddRange(programArgs);
        }

        // ReDOS is a GUI-subsystem program, so Windows gives it no console of its own. Launched
        // directly by the terminal it would have nothing to draw on, and the DOS core would end up
        // allocating a default-sized console that ReDOS never shaped or cleared — which is what
        // leaves old frames smeared behind new ones. Going through cmd, a console-subsystem
        // program, creates a real console for the session to inherit and control.
        string inner = string.Join(' ', new[] { AppPaths.ExecutablePath }.Concat(innerArgs).Select(Quote));

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
            psi.ArgumentList.Add("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(inner);
        }
        else
        {
            // "start" gives the child its own console window; the inner cmd is what hosts it.
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(inner);
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
        // it has no built-in shell, so the sandbox needs a real COMMAND.COM. ReDOS installs one.
        bool isShellSession = programPath is null;
        if (isShellSession)
        {
            try
            {
                programPath = DosShell.EnsureAsync(report.Status).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                report.Error(
                    $"A DOS shell could not be installed ({ex.Message}).\n" +
                    "Opening the graphical DOS machine instead.");
                return Launcher.RunPrompt(report);
            }
        }

        // Give DOS a clean drive root: the sandbox becomes a drive letter of its own, so programs
        // see PROGRAMS\FOO at the root instead of a long Windows path.
        using var drive = SubstDrive.Create(Sandbox.Root);

        // A shell session starts at the root of the machine; a program starts in its own folder.
        string workingDirectory = isShellSession
            ? Sandbox.Root
            : Path.GetDirectoryName(programPath) ?? Sandbox.Root;

        string program = programPath!;

        if (drive is not null && Sandbox.Contains(programPath!))
        {
            workingDirectory = drive.Translate(workingDirectory);
            program = drive.Translate(programPath!);
        }

        var psi = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add(program);
        foreach (string arg in programArgs) psi.ArgumentList.Add(arg);

        ApplyDosEnvironment(psi, drive);

        // Shape the console to the DOS screen before handing it over, or the program's own screen
        // clears will not cover it and frames will pile up.
        ConsoleLayout.ApplyDosScreen();

        var started = DateTime.UtcNow;
        int exitCode;
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return 1;
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            report.Error($"The console DOS core failed to start: {ex.Message}");
            return 3;
        }

        // A program that gives up within a few seconds has almost always hit the one thing a
        // terminal cannot provide: graphics. The window is still ours, so ask before it closes.
        if (!isShellSession && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            return OfferGraphicalFallback(programPath!, programArgs, exitCode, report);

        return exitCode;
    }

    private static int OfferGraphicalFallback(string programPath, IReadOnlyList<string> programArgs, int exitCode, Reporter report)
    {
        string name = Path.GetFileName(programPath);

        Console.WriteLine();
        Console.WriteLine($"{name} stopped almost immediately.");
        Console.WriteLine("Programs that draw graphics cannot run in a terminal: the console core");
        Console.WriteLine("emulates no video hardware, so they quit at startup.");
        Console.WriteLine();

        if (!report.Confirm("Open it in the graphical DOS machine instead (sound and graphics work there)?"))
        {
            Console.WriteLine();
            Console.WriteLine($"Leaving it as it is. To try graphics later:  redos run {name} --gui");
            return exitCode;
        }

        // Remember, so this program goes straight to the graphical core from now on.
        ProgramPreferences.SetGraphical(programPath);
        Console.WriteLine();
        Console.WriteLine($"Opening the graphical machine. {name} will use it automatically from now on.");

        return Launcher.Run(programPath, programArgs, new RunOptions { Graphical = true, ForceDos = true }, report);
    }

    /// <summary>
    /// Give DOS a small, deliberate environment. The inherited Windows one is both irrelevant and
    /// dangerous here: the DOS environment block is a few hundred bytes, and a modern PATH alone can
    /// overflow it.
    /// </summary>
    private static void ApplyDosEnvironment(ProcessStartInfo psi, SubstDrive? drive)
    {
        // Windows APIs inside the console core still need these two.
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        string? windir = Environment.GetEnvironmentVariable("windir");

        psi.Environment.Clear();
        if (systemRoot is not null) psi.Environment["SystemRoot"] = systemRoot;
        if (windir is not null) psi.Environment["windir"] = windir;

        string root = drive is not null ? $"{drive.Letter}:" : Path.GetPathRoot(Sandbox.Root)?.TrimEnd('\\') ?? "C:";
        string dosDir = drive is not null ? drive.Translate(Sandbox.DosDir) : Sandbox.DosDir;
        string tempDir = drive is not null ? drive.Translate(Sandbox.TempDir) : Sandbox.TempDir;

        psi.Environment["PATH"] = $"{dosDir};{root}\\";
        psi.Environment["TEMP"] = tempDir;
        psi.Environment["TMP"] = tempDir;
        psi.Environment["COMSPEC"] = DosShell.Find() ?? Path.Combine(Sandbox.DosDir, DosShell.ShellFileName);

        // Marks every ReDOS session at the prompt itself: "(ReDOS) M:\>".
        psi.Environment["PROMPT"] = "(ReDOS) $P$G";
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

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
internal sealed partial class SubstDrive : IDisposable
{
    private readonly string _target;

    /// <summary>False when we adopted a mapping someone else made, which we must not tear down.</summary>
    private readonly bool _owned;

    internal string Letter { get; }

    private SubstDrive(string letter, string target, bool owned)
    {
        Letter = letter;
        _target = target;
        _owned = owned;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint QueryDosDevice(string lpDeviceName, char[]? lpTargetPath, uint ucchMax);

    /// <summary>Where a drive letter actually points, for substituted drives.</summary>
    private static string? TargetOf(string letter)
    {
        var buffer = new char[1024];
        uint length = QueryDosDevice($"{letter}:", buffer, (uint)buffer.Length);
        if (length == 0) return null;

        string target = new string(buffer, 0, (int)length).Split('\0')[0];

        // subst reports its target as a \??\ prefixed path; real volumes report a device path.
        const string prefix = @"\??\";
        return target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : null;
    }

    private static string[] Candidates()
    {
        string? letter = Environment.GetEnvironmentVariable("REDOS_DRIVE")?.TrimEnd(':', '\\');
        return letter is { Length: 1 }
            ? [letter.ToUpperInvariant()]
            : ["M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y"];
    }

    /// <summary>The letter a session is expected to land on, for labelling a window before it opens.</summary>
    internal static string PlannedLetter
    {
        get
        {
            var taken = DriveInfo.GetDrives().Select(d => d.Name[..1].ToUpperInvariant()).ToHashSet();
            return Candidates().FirstOrDefault(c => !taken.Contains(c)) ?? "C";
        }
    }

    /// <summary>Map <paramref name="folder"/> to a free drive letter, or null if none could be taken.</summary>
    internal static SubstDrive? Create(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

        var taken = DriveInfo.GetDrives()
            .Select(d => d.Name[..1].ToUpperInvariant())
            .ToHashSet();

        // A session that was killed rather than closed leaves its mapping behind. Adopt it instead
        // of walking down the alphabet on every launch.
        foreach (string candidate in Candidates())
        {
            if (!taken.Contains(candidate)) continue;
            if (string.Equals(TargetOf(candidate)?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
                return new SubstDrive(candidate, full, owned: false);
        }

        foreach (string candidate in Candidates())
        {
            if (taken.Contains(candidate)) continue;
            if (!RunSubst($"{candidate}:", folder)) continue;
            return new SubstDrive(candidate, full, owned: true);
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
        // Another session may still be using a mapping we merely adopted.
        if (!_owned) return;

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
