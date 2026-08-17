namespace ReDOS;

/// <summary>
/// A Windows installation living inside the sandbox, used to run 16-bit Windows (NE) programs.
///
/// Only Windows 3.x can be started from a mounted folder: it is a DOS program that happens to put
/// the machine into protected mode, so <c>WIN.COM</c> runs like anything else. Windows 95 and later
/// are operating systems that boot a disk — they need a hard disk image and a real boot, which is
/// what <see cref="Launcher.BootImage"/> is for.
/// </summary>
internal static class WindowsInstall
{
    internal enum Flavour
    {
        None,
        /// <summary>Windows 3.0/3.1/3.11 — runnable from the sandbox folder.</summary>
        Windows3,
        /// <summary>Windows 95/98/ME — present, but only bootable from a disk image.</summary>
        Windows9x,
    }

    internal sealed record Info(Flavour Flavour, string Directory, string? WinCom)
    {
        internal bool CanRunFromFolder => Flavour == Flavour.Windows3 && WinCom is not null;
    }

    /// <summary>Look for a Windows installation in the sandbox.</summary>
    internal static Info Detect()
    {
        foreach (string directory in CandidateDirectories())
        {
            if (!Directory.Exists(directory)) continue;

            string winCom = Path.Combine(directory, "WIN.COM");
            if (!File.Exists(winCom)) continue;

            return new Info(Classify(directory), directory, winCom);
        }

        return new Info(Flavour.None, Sandbox.Root, null);
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(Sandbox.Root, "WINDOWS");
        yield return Path.Combine(Sandbox.Root, "WIN");
        yield return Path.Combine(Sandbox.Root, "WIN31");
    }

    /// <summary>
    /// Tell the two eras apart by what only the later one has: a virtual machine manager, a shell
    /// called Explorer, and the folders a 9x desktop keeps.
    /// </summary>
    private static Flavour Classify(string directory)
    {
        string[] nineXMarkers =
        [
            Path.Combine(directory, "SYSTEM", "VMM32.VXD"),
            Path.Combine(directory, "EXPLORER.EXE"),
            Path.Combine(directory, "IFSHLP.SYS"),
        ];

        int hits = nineXMarkers.Count(marker => File.Exists(marker) || Directory.Exists(marker));
        return hits >= 2 ? Flavour.Windows9x : Flavour.Windows3;
    }

    internal static string Describe(Info info) => info.Flavour switch
    {
        Flavour.Windows3 => $"Windows 3.x in {info.Directory}",
        Flavour.Windows9x => $"Windows 95/98 in {info.Directory} (needs a disk image to boot)",
        _ => "none",
    };

    /// <summary>The DOS command that starts Windows, optionally with a program to run inside it.</summary>
    internal static string StartCommand(Info info, string? programDosPath)
    {
        string winDirectory = Sandbox.ToDosPath(info.Directory);
        string command = $"{winDirectory}\\WIN.COM";

        // Windows 3.x takes the program to run straight on its command line.
        return programDosPath is null ? command : $"{command} {programDosPath}";
    }
}
