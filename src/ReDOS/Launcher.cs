using System.ComponentModel;
using System.Diagnostics;

namespace ReDOS;

internal enum Backend
{
    /// <summary>32-bit Windows still ships NTVDM: let the OS run the program for real.</summary>
    NativeNtvdm,
    /// <summary>Everything else — a bundled core, driven silently.</summary>
    BundledCore,
}

internal sealed record RunOptions
{
    /// <summary>Treat the target as a DOS program even if the header says otherwise.</summary>
    internal bool ForceDos { get; init; }

    /// <summary>Leave the program where it is and mount its folder as D: instead of importing it.</summary>
    internal bool NoImport { get; init; }

    /// <summary>Drop to the DOS prompt when the program exits rather than closing the window.</summary>
    internal bool StayOpen { get; init; }

    /// <summary>Print the machine configuration that would be used and stop. For troubleshooting.</summary>
    internal bool DryRun { get; init; }

    /// <summary>
    /// Use the graphical core's own window instead of a terminal. Needed for anything with graphics
    /// or sound, since the console core emulates neither.
    /// </summary>
    internal bool Graphical { get; init; }
}

internal static class Launcher
{
    /// <summary>
    /// The entry point behind every association ReDOS registers. Non-DOS programs are handed straight
    /// back to Windows, so intercepting a launch is always safe.
    /// </summary>
    internal static int Run(string target, IReadOnlyList<string> programArgs, RunOptions options, Reporter report)
    {
        string full;
        try
        {
            full = Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            report.Error($"Not a usable path: {target}");
            return 2;
        }

        ProgramKind kind = DosDetector.Detect(full);
        if (kind == ProgramKind.Missing)
        {
            report.Error($"File not found: {full}");
            return 2;
        }

        if (!options.ForceDos && !DosDetector.IsDosKind(kind))
        {
            AppPaths.Log($"passthrough ({kind}): {full}");
            return Passthrough(full, programArgs, report);
        }

        AppPaths.Log($"dos launch ({kind}): {full}");
        Sandbox.Ensure();

        string programPath = full;
        string? externalMount = null;

        if (!Sandbox.Contains(full))
        {
            if (options.NoImport)
            {
                externalMount = Path.GetDirectoryName(full);
            }
            else
            {
                try
                {
                    var imported = Sandbox.Import(full);
                    programPath = imported.HostPath;

                    string extra = imported.SupportFilesCopied > 0
                        ? $" (+{imported.SupportFilesCopied} data file(s))"
                        : "";
                    report.Status(imported.WasAlreadyPresent
                        ? $"Using the copy already in the sandbox: C:\\PROGRAMS\\{imported.ProgramName}{extra}"
                        : $"Imported into the sandbox as C:\\PROGRAMS\\{imported.ProgramName}{extra}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Importing is a convenience; never let it be the reason a program will not start.
                    AppPaths.Log($"import failed, mounting in place: {ex.Message}");
                    externalMount = Path.GetDirectoryName(full);
                }
            }
        }

        var plan = new LaunchPlan
        {
            ProgramPath = programPath,
            ProgramArgs = programArgs,
            AsBatch = kind == ProgramKind.Batch,
            ExternalMount = externalMount,
            StayOpen = options.StayOpen,
            Title = Path.GetFileNameWithoutExtension(programPath),
        };

        if (options.DryRun)
        {
            string profile = ProfileBuilder.Build(plan);
            report.Info($"# {profile}\n\n{File.ReadAllText(profile)}");
            return 0;
        }

        // A terminal is the default home for a DOS program. The session falls back to the graphical
        // core by itself if the console core cannot be had, so this is always safe to try.
        if (!options.Graphical && SelectBackend() == Backend.BundledCore)
            return Terminal.OpenDosSession(programPath, programArgs, report);

        return SelectBackend() == Backend.NativeNtvdm
            ? RunViaNtvdm(programPath, programArgs, report)
            : RunViaCore(plan, report);
    }

    /// <summary>Open the sandbox at a bare DOS prompt, with no program to run.</summary>
    internal static int RunPrompt(Reporter report)
    {
        Sandbox.Ensure();
        return RunViaCore(new LaunchPlan { Title = "ReDOS - MS-DOS prompt", StayOpen = true }, report);
    }

    /// <summary>
    /// Open a machine with floppies in drive A:, for running an installer off disk 1. This always
    /// uses the graphical core: the console core has no concept of a disk image.
    /// </summary>
    internal static int RunFloppies(IReadOnlyList<string> images, Reporter report)
    {
        Sandbox.Ensure();

        var plan = new LaunchPlan
        {
            Title = $"ReDOS - {FloppyImage.SetName(images[0])} (A:)",
            FloppyImages = images,
            StartOnFloppy = true,
            StayOpen = true,
        };

        return RunViaCore(plan, report);
    }

    internal static Backend SelectBackend()
    {
        // Virtual-8086 mode does not exist in x64 long mode, which is why 64-bit Windows dropped NTVDM.
        if (Environment.Is64BitOperatingSystem) return Backend.BundledCore;

        string ntvdm = Path.Combine(Environment.SystemDirectory, "ntvdm.exe");
        return File.Exists(ntvdm) ? Backend.NativeNtvdm : Backend.BundledCore;
    }

    private static int RunViaNtvdm(string full, IReadOnlyList<string> programArgs, Reporter report)
    {
        // The program has already been imported, so it still reads and writes inside the sandbox.
        var psi = new ProcessStartInfo(full)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
        };
        foreach (string arg in programArgs) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            report.Error($"NTVDM could not start the program: {ex.Message}");
            return 1;
        }
    }

    private static int RunViaCore(LaunchPlan plan, Reporter report)
    {
        string core;
        try
        {
            core = CoreProvider.EnsureAsync(report.Status).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report.Error(
                "ReDOS could not obtain its DOS core.\n\n" +
                $"{ex.Message}\n\n" +
                "Check your internet connection, or download the full ReDOS release " +
                "(which already contains the core) instead of the standalone exe.");
            return 3;
        }

        string profile = ProfileBuilder.Build(plan);

        var psi = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(core)!,
        };
        psi.ArgumentList.Add("-conf");
        psi.ArgumentList.Add(profile);
        psi.ArgumentList.Add("-fastlaunch");
        psi.ArgumentList.Add("-nolog");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                report.Error("The DOS core failed to start.");
                return 3;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            report.Error($"The DOS core failed to start: {ex.Message}");
            return 3;
        }
    }

    /// <summary>
    /// Start a non-DOS program the way Windows would have. Deliberately bypasses ShellExecute's
    /// registry lookup so an intercepted association cannot loop back into ReDOS.
    /// </summary>
    private static int Passthrough(string full, IReadOnlyList<string> programArgs, Reporter report)
    {
        var psi = new ProcessStartInfo(full)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
        };
        foreach (string arg in programArgs) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            return process is null ? 1 : 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // Requires elevation — re-launch through the shell so the UAC prompt appears.
            var elevated = new ProcessStartInfo(full)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', programArgs.Select(Quote)),
                WorkingDirectory = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
            };
            try
            {
                using var process = Process.Start(elevated);
                return process is null ? 1 : 0;
            }
            catch (Win32Exception inner)
            {
                report.Error($"Could not start {Path.GetFileName(full)}: {inner.Message}");
                return 1;
            }
        }
        catch (Win32Exception ex)
        {
            report.Error($"Could not start {Path.GetFileName(full)}: {ex.Message}");
            return 1;
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;
}
