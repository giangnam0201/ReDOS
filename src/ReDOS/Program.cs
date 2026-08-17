using System.Windows.Forms;

namespace ReDOS;

internal static class Program
{
    private const string Usage = """
        ReDOS - run MS-DOS programs on modern Windows, with nothing to set up.

          redos                        Open the MS-DOS machine in a terminal window.
          redos --gui                  Open it in the graphical machine's own window instead.
          redos [run] FILE [OPTS] [ARGS...]
                                       Run FILE in a terminal. Non-DOS programs go to Windows.
                                       "run" is optional and options may go anywhere;
                                       put DOS program arguments after "--".
                                         --gui         use the graphical core's own window instead
                                                       (needed for graphics and sound)
                                         --force-dos   run it as DOS even if the header disagrees
                                         --no-import   leave it in place, mounted as D:, not imported
                                         --stay        stay at the DOS prompt after it exits
                                         --dry-run     print the machine config instead of running
          redos win [PROGRAM]          Start Windows 3.x from the sandbox, optionally opening a
                                       16-bit Windows program in it.
          redos boot IMAGE             Boot a hard disk image — how Windows 95/98 has to run.
                                         --machine NAME  emulated video card (svga_s3, svga_et4000,
                                                         svga_paradise, vgaonly...)
                                         --vmem MB       video memory; a mismatch here makes the
                                                         guest draw at the wrong width
          redos manager                Open the sandbox manager (add, run and delete programs).
          redos import FILE|IMAGE...    Copy a program into the sandbox. Floppy images are
                                       unpacked straight in, no installer needed.
          redos mount IMAGE|FOLDER...  Put floppies in drive A: and open a DOS machine, for
                                       running an installer. Ctrl+F4 changes disk.
                                         --extract  unpack the disks instead of booting
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
          redos shell [--update | --install PATH]
                                       Show or replace the DOS shell (COMMAND.COM).
          redos console-core [--update | --install PATH]
                                       Show, fetch or replace the console DOS core.
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
            // "shell" manages the COMMAND.COM binary; "dos" and "prompt" open the machine.
            "dos" or "prompt" => OpenMachine(rest, report),
            "session" => SessionCommand(rest, report),
            "manager" or "gui" => ManagerCommand(),
            "import" => ImportCommand(rest, report),
            "mount" or "floppy" or "disk" => MountCommand(rest, report),
            "win" or "windows" => WindowsCommand(rest, report),
            "boot" => BootCommand(rest, report),
            "list" or "ls" => ListCommand(report),
            "remove" or "rm" => RemoveCommand(rest, report),
            "open" or "explorer" => OpenSandboxCommand(),
            "reset" => ResetCommand(report),
            "detect" => DetectCommand(rest, report),
            "status" => StatusCommand(report),
            "install" => InstallCommand(rest, report),
            "uninstall" => UninstallCommand(report),
            "core" => CoreCommand(rest, report),
            "shell" => ShellCommand(rest, report),
            "console-core" => ConsoleCoreCommand(rest, report),
            "-v" or "--version" or "version" => Print(report, Version()),
            "-h" or "--help" or "help" or "/?" => Print(report, Usage),
            // No verb: Explorer, drag-and-drop and "redos GAME.EXE" all land here. Anything that
            // names a real file is a request to run it, whatever order the options came in;
            // options on their own ("redos --gui") are about the machine itself.
            _ when args.Any(a => !a.StartsWith('-') && File.Exists(a)) => RunCommand(args, report),
            _ when args.All(IsMachineOption) => OpenMachine(args, report),
            _ => Print(report, Usage, exitCode: 2),
        };
    }

    /// <summary>Options that choose how the machine opens rather than naming a program to run.</summary>
    private static bool IsMachineOption(string arg) =>
        arg is "--gui" or "--window" or "-g" or "--console" or "--terminal";

    /// <summary>
    /// The machine itself. A console-hosted core turns a real terminal window into DOS; without one,
    /// ReDOS opens the graphical machine, which behaves the same but draws its own window.
    /// </summary>
    private static int OpenMachine(string[] args, Reporter report)
    {
        EnsureFirstRun(report);

        if (args.Any(a => a is "--window" or "--gui" or "-g"))
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
        var remaining = new List<string>(args.Length);

        // ReDOS options are recognised wherever they appear, because "redos GAME.EXE --gui" is how
        // people actually type it. DOS programs take /switches rather than --options, so there is
        // nothing to collide with; "--" still forces everything after it through to the program.
        bool passThrough = false;

        foreach (string arg in args)
        {
            if (!passThrough)
            {
                if (arg == "--") { passThrough = true; continue; }

                switch (arg)
                {
                    case "--force-dos" or "-f": options = options with { ForceDos = true }; continue;
                    case "--no-import": options = options with { NoImport = true }; continue;
                    case "--stay": options = options with { StayOpen = true }; continue;
                    case "--dry-run": options = options with { DryRun = true }; continue;
                    case "--console" or "--terminal": options = options with { Graphical = false, ForceConsole = true }; continue;
                    case "--gui" or "--window" or "-g": options = options with { Graphical = true }; continue;
                }
            }

            remaining.Add(arg);
        }

        if (remaining.Count == 0) return Print(report, Usage, exitCode: 2);

        EnsureFirstRun(report);
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
            var images = CollectImages(args);
            if (images.Count > 0)
            {
                var extracted = Sandbox.ImportImages(images);
                return Print(report,
                    $"Unpacked {images.Count} disk(s), {extracted.SupportFilesCopied} file(s), " +
                    $"into C:\\PROGRAMS\\{extracted.ProgramName}.\n" +
                    "No installer needed — run it with:  redos run " +
                    (extracted.HostPath.EndsWith(extracted.ProgramName, StringComparison.OrdinalIgnoreCase)
                        ? extracted.HostPath
                        : Path.GetFileName(extracted.HostPath)));
            }

            string target = Path.GetFullPath(args[0]);
            var result = Directory.Exists(target)
                ? Sandbox.ImportFolder(target)
                : Sandbox.Import(target);

            string extra = result.SupportFilesCopied > 0 ? $" with {result.SupportFilesCopied} data file(s)" : "";
            return Print(report, result.WasAlreadyPresent
                ? $"Already in the sandbox as C:\\PROGRAMS\\{result.ProgramName}{extra}."
                : $"Imported as C:\\PROGRAMS\\{result.ProgramName}{extra}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            report.Error($"Could not import: {ex.Message}");
            return 1;
        }
    }

    private static int MountCommand(string[] args, Reporter report)
    {
        var paths = args.Where(a => !a.StartsWith('-')).ToArray();
        if (paths.Length == 0) return Print(report, Usage, exitCode: 2);

        var images = CollectImages(paths);
        if (images.Count == 0)
        {
            report.Error(
                "No readable floppy images found.\n" +
                "Pass one or more .img/.ima files, or a folder containing them.");
            return 2;
        }

        EnsureFirstRun(report);

        if (args.Contains("--extract"))
        {
            var extracted = Sandbox.ImportImages(images);
            return Print(report,
                $"Unpacked {images.Count} disk(s), {extracted.SupportFilesCopied} file(s), " +
                $"into C:\\PROGRAMS\\{extracted.ProgramName}.");
        }

        report.Status($"Mounting {images.Count} disk(s) as A:, sandbox as C:.");
        return Launcher.RunFloppies(images, report);
    }

    private static int WindowsCommand(string[] args, Reporter report)
    {
        EnsureFirstRun(report);
        string? program = args.FirstOrDefault(a => !a.StartsWith('-'));
        return Launcher.RunWindows(program, report);
    }

    private static int BootCommand(string[] args, Reporter report)
    {
        string? image = null;
        string? machine = null;
        int? videoMemory = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--machine" when i + 1 < args.Length: machine = args[++i]; continue;
                case "--vmem" when i + 1 < args.Length && int.TryParse(args[i + 1], out int mb):
                    videoMemory = mb;
                    i++;
                    continue;
            }

            if (!args[i].StartsWith('-')) image ??= args[i];
        }

        if (image is null) return Print(report, Usage, exitCode: 2);

        EnsureFirstRun(report);
        return Launcher.BootImage(image, report, machine, videoMemory);
    }

    /// <summary>Expand the given paths into a disk set: files as given, folders scanned for images.</summary>
    private static IReadOnlyList<string> CollectImages(IEnumerable<string> paths)
    {
        var found = new List<string>();
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                found.AddRange(Directory
                    .EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Where(f => FloppyImage.LooksLikeImage(f) && FloppyImage.CanRead(f)));
            }
            else if (FloppyImage.LooksLikeImage(full) && FloppyImage.CanRead(full))
            {
                found.Add(full);
            }
        }

        return FloppyImage.SortSet(found);
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
             Windows in sandbox : {WindowsInstall.Describe(WindowsInstall.Detect())}
             Windows Terminal   : {terminal}
             On PATH            : {(PathIntegration.Contains(Path.GetDirectoryName(AppPaths.ExecutablePath) ?? AppPaths.InstallDir) ? "yes - \"redos\" works from any shell" : "no - run: redos install")}
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

    private static int ShellCommand(string[] args, Reporter report)
    {
        int index = Array.IndexOf(args, "--install");
        if (index >= 0 && index + 1 < args.Length)
        {
            try
            {
                string installed = DosShell.InstallFrom(Path.GetFullPath(args[index + 1]));
                return Print(report, $"DOS shell installed: {installed}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                report.Error($"Could not install the shell: {ex.Message}");
                return 1;
            }
        }

        if (args.Contains("--update"))
        {
            try
            {
                string updated = DosShell.EnsureAsync(report.Status, force: true).GetAwaiter().GetResult();
                return Print(report, $"DOS shell updated: {updated}");
            }
            catch (Exception ex)
            {
                report.Error($"Could not update the shell: {ex.Message}");
                return 3;
            }
        }

        string? existing = DosShell.Find();
        return Print(report, existing is null
            ? "No DOS shell installed yet; FreeCOM is fetched automatically the first time you open the prompt."
            : $"DOS shell: {existing}");
    }

    private static int ConsoleCoreCommand(string[] args, Reporter report)
    {
        int index = Array.IndexOf(args, "--install");
        if (index < 0 || index + 1 >= args.Length)
        {
            if (!args.Contains("--update"))
            {
                string? existing = ConsoleCore.Find();
                return Print(report, existing is null
                    ? "No console core installed yet; it is fetched automatically the first time you run a DOS program."
                    : $"Console core: {existing}");
            }

            try
            {
                string fetched = ConsoleCore.EnsureAsync(report.Status, force: true).GetAwaiter().GetResult();
                return Print(report, $"Console core updated: {fetched}");
            }
            catch (Exception ex)
            {
                report.Error($"Could not update the console core: {ex.Message}");
                return 3;
            }
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
