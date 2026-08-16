using System.Windows.Forms;

namespace ReDOS;

internal static class Program
{
    private const string Usage = """
        ReDOS - run MS-DOS programs on modern Windows, with nothing to set up.

          redos                        Open the MS-DOS machine (a terminal window, the sandbox as C:).
          redos run [OPTS] FILE [ARGS...]
                                       Run FILE. Non-DOS programs are passed straight to Windows.
                                         --force-dos   run it as DOS even if the header disagrees
                                         --no-import   leave it in place, mounted as D:, not imported
                                         --console     run it in this terminal instead of a window
                                         --stay        stay at the DOS prompt after it exits
                                         --dry-run     print the machine config instead of running
          redos manager                Open the sandbox manager (add, run and delete programs).
          redos import FILE            Copy a program and its data files into the sandbox.
          redos list                   List the programs in the sandbox.
          redos remove NAME            Delete a program from the sandbox.
          redos open                   Open the sandbox folder (drive C:) in Explorer.
          redos reset                  Erase the sandbox and start from a fresh machine.
          redos detect FILE            Report what kind of executable FILE is.
          redos status                 Show what is installed and where.
          redos install [--intercept-exe] / redos uninstall
                                       Add or remove the file associations. --intercept-exe also
                                       routes double-clicked .exe files through ReDOS (reversible).
          redos core [--update]        Show or refresh the graphical DOS core.
          redos console-core --install PATH
                                       Register a console-hosted DOS core (msdos-player).
          redos --version
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        // Bind to the parent console *before* anything writes output, so ReDOS behaves like a normal
        // CLI from a terminal while staying popup-free when launched from Explorer.
        bool hasConsole = ConsoleHost.Attach();
        var report = new Reporter(hasConsole);

        try
        {
            return Dispatch(args, report);
        }
        catch (Exception ex)
        {
            report.Error($"Unexpected failure: {ex.Message}");
            AppPaths.Log(ex.ToString());
            return 1;
        }
        finally
        {
            ConsoleHost.FinishOutput();
        }
    }

    private static int Dispatch(string[] args, Reporter report)
    {
        if (args.Length == 0) return OpenMachine([], report);

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        return command switch
        {
            "run" => RunCommand(rest, report),
            "dos" or "shell" or "prompt" => OpenMachine(rest, report),
            "session" => SessionCommand(rest, report),
            "manager" or "gui" => ManagerCommand(),
            "import" => ImportCommand(rest, report),
            "list" or "ls" => ListCommand(report),
            "remove" or "rm" => RemoveCommand(rest, report),
            "open" or "explorer" => OpenSandboxCommand(),
            "reset" => ResetCommand(report),
            "detect" => DetectCommand(rest, report),
            "status" => StatusCommand(report),
            "install" => InstallCommand(rest, report),
            "uninstall" => UninstallCommand(report),
            "core" => CoreCommand(rest, report),
            "console-core" => ConsoleCoreCommand(rest, report),
            "-v" or "--version" or "version" => Print(report, Version()),
            "-h" or "--help" or "help" or "/?" => Print(report, Usage),
            // Bare path: Explorer and drag-and-drop hand us the file with no verb.
            _ => File.Exists(args[0]) ? RunCommand(args, report) : Print(report, Usage, exitCode: 2),
        };
    }

    /// <summary>
    /// The machine itself. A console-hosted core turns a real terminal window into DOS; without one,
    /// ReDOS opens the graphical machine, which behaves the same but draws its own window.
    /// </summary>
    private static int OpenMachine(string[] args, Reporter report)
    {
        EnsureFirstRun(report);

        if (args.Contains("--window") || !Terminal.CanHostDosSession)
            return Launcher.RunPrompt(report);

        return Terminal.OpenDosSession(programPath: null, [], report);
    }

    /// <summary>Internal verb: the terminal window we just opened calls this to become the machine.</summary>
    private static int SessionCommand(string[] args, Reporter report)
    {
        string? program = args.Length > 0 ? args[0] : null;
        string[] programArgs = args.Length > 1 ? args[1..] : [];
        return Terminal.RunInThisConsole(program, programArgs, report);
    }

    /// <summary>
    /// Set ReDOS up the first time it runs. Everything here is per-user and reversible, so it happens
    /// without asking — "no setup needed" is the entire point of the program.
    /// </summary>
    private static void EnsureFirstRun(Reporter report)
    {
        Sandbox.Ensure();
        if (ShellIntegration.IsInstalled()) return;

        try
        {
            ShellIntegration.Install(interceptExe: false, report);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"first-run association setup failed: {ex.Message}");
        }
    }

    private static int RunCommand(string[] args, Reporter report)
    {
        var options = new RunOptions();
        bool console = false;
        var remaining = new List<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            // Only treat flags before the target path as ours; everything after belongs to the DOS program.
            if (remaining.Count == 0 && args[i].StartsWith('-'))
            {
                switch (args[i])
                {
                    case "--force-dos" or "-f": options = options with { ForceDos = true }; continue;
                    case "--no-import": options = options with { NoImport = true }; continue;
                    case "--stay": options = options with { StayOpen = true }; continue;
                    case "--dry-run": options = options with { DryRun = true }; continue;
                    case "--console": console = true; continue;
                }
            }

            remaining.Add(args[i]);
        }

        if (remaining.Count == 0) return Print(report, Usage, exitCode: 2);

        EnsureFirstRun(report);

        if (console)
        {
            string target = Path.GetFullPath(remaining[0]);
            var imported = Sandbox.Contains(target) ? null : (Sandbox.ImportResult?)Sandbox.Import(target);
            return Terminal.OpenDosSession(imported?.HostPath ?? target, remaining[1..], report);
        }

        return Launcher.Run(remaining[0], remaining[1..], options, report);
    }

    private static int ManagerCommand()
    {
        EnsureFirstRun(new Reporter(console: false));
        ApplicationConfiguration.Initialize();
        Application.Run(new ManagerForm());
        return 0;
    }

    private static int ImportCommand(string[] args, Reporter report)
    {
        if (args.Length == 0) return Print(report, Usage, exitCode: 2);

        try
        {
            var result = Sandbox.Import(Path.GetFullPath(args[0]));
            string extra = result.SupportFilesCopied > 0 ? $" with {result.SupportFilesCopied} data file(s)" : "";
            return Print(report, result.WasAlreadyPresent
                ? $"Already in the sandbox as C:\\PROGRAMS\\{result.ProgramName}{extra}."
                : $"Imported as C:\\PROGRAMS\\{result.ProgramName}{extra}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            report.Error($"Could not import: {ex.Message}");
            return 1;
        }
    }

    private static int ListCommand(Reporter report)
    {
        var programs = Sandbox.ListPrograms();
        if (programs.Count == 0)
            return Print(report, $"The sandbox has no programs yet. Add one with:  redos import <file>\nSandbox: {Sandbox.Root}");

        var lines = programs.Select(p =>
            $"  {p.Name,-10}  C:\\PROGRAMS\\{p.Name,-12}  {(p.Executable is null ? "(no executable)" : Path.GetFileName(p.Executable))}");

        return Print(report, $"Programs in the sandbox ({Sandbox.Root}):\n" + string.Join('\n', lines));
    }

    private static int RemoveCommand(string[] args, Reporter report)
    {
        if (args.Length == 0) return Print(report, Usage, exitCode: 2);

        try
        {
            Sandbox.RemoveProgram(args[0]);
            return Print(report, $"Deleted C:\\PROGRAMS\\{args[0]}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            report.Error(ex.Message);
            return 1;
        }
    }

    private static int OpenSandboxCommand()
    {
        Sandbox.OpenInExplorer();
        return 0;
    }

    private static int ResetCommand(Reporter report)
    {
        if (!report.Confirm(
                $"Erase the sandbox and start from a fresh MS-DOS machine?\n\nEverything in {Sandbox.Root} " +
                "will be deleted, including imported programs and save files."))
        {
            return 1;
        }

        Sandbox.Reset();
        return Print(report, "The sandbox is now a fresh machine.");
    }

    private static int DetectCommand(string[] args, Reporter report)
    {
        if (args.Length == 0) return Print(report, Usage, exitCode: 2);

        ProgramKind kind = DosDetector.Detect(args[0]);
        string verdict = DosDetector.IsDosKind(kind)
            ? "ReDOS will run this."
            : "ReDOS will hand this to Windows.";

        return Print(report, $"{Path.GetFileName(args[0])}: {DosDetector.Describe(kind)} ({kind}). {verdict}",
            exitCode: kind == ProgramKind.Missing ? 2 : 0);
    }

    private static int StatusCommand(Reporter report)
    {
        string core = CoreProvider.Find() ?? "not downloaded yet (fetched automatically on first use)";
        string consoleCore = ConsoleCore.Find() ?? "not installed (the graphical machine is used instead)";
        string terminal = Terminal.FindWindowsTerminal() ?? "not found (a classic console window is used)";

        return Print(report,
            $"""
             {Version()}

             Sandbox (drive C:) : {Sandbox.Root}
             Programs           : {Sandbox.ListPrograms().Count}
             Backend            : {(Launcher.SelectBackend() == Backend.NativeNtvdm ? "native NTVDM (32-bit Windows)" : "bundled DOS core")}
             Graphical core     : {core}
             Console core       : {consoleCore}
             Windows Terminal   : {terminal}
             Associations       : {(ShellIntegration.IsInstalled() ? "enabled" : "not enabled - run: redos install")}
             .exe interception  : {(ShellIntegration.IsInterceptingExe() ? "on" : "off (turn on with: redos install --intercept-exe)")}
             Log                : {AppPaths.LogFile}
             """);
    }

    private static int InstallCommand(string[] args, Reporter report)
    {
        bool intercept = args.Contains("--intercept-exe");
        Sandbox.Ensure();
        ShellIntegration.Install(intercept, report);
        return Print(report, intercept
            ? "Done. Double-clicked .exe files now go through ReDOS (DOS ones run, the rest start normally)."
            : "Done. DOS file types are now associated with ReDOS.");
    }

    private static int UninstallCommand(Reporter report)
    {
        ShellIntegration.Uninstall(report);
        return Print(report,
            $"Removed every registry change.\nYour sandbox and its files are untouched in {Sandbox.Root} — " +
            "delete that folder if you want them gone too.");
    }

    private static int CoreCommand(string[] args, Reporter report)
    {
        bool update = args.Contains("--update");
        if (!update)
        {
            string? existing = CoreProvider.Find();
            return Print(report, existing is null
                ? "No DOS core installed yet. Run \"redos core --update\" to fetch one."
                : $"DOS core: {existing}");
        }

        try
        {
            string core = CoreProvider.EnsureAsync(report.Status, force: true).GetAwaiter().GetResult();
            return Print(report, $"DOS core updated: {core}");
        }
        catch (Exception ex)
        {
            report.Error($"Could not update the DOS core: {ex.Message}");
            return 3;
        }
    }

    private static int ConsoleCoreCommand(string[] args, Reporter report)
    {
        int index = Array.IndexOf(args, "--install");
        if (index < 0 || index + 1 >= args.Length)
        {
            string? existing = ConsoleCore.Find();
            return Print(report, existing is null
                ? "No console core installed. Install one with:\n  redos console-core --install <path to msdos-player.exe or its zip>"
                : $"Console core: {existing}");
        }

        try
        {
            string path = ConsoleCore.InstallFrom(Path.GetFullPath(args[index + 1]));
            return Print(report, $"Console core installed: {path}\n\"redos\" will now open the DOS machine in a terminal.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            report.Error($"Could not install the console core: {ex.Message}");
            return 1;
        }
    }

    private static string Version() =>
        $"ReDOS {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    private static int Print(Reporter report, string message, int exitCode = 0)
    {
        if (exitCode == 0) report.Info(message);
        else report.Error(message);
        return exitCode;
    }
}
