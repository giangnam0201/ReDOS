using System.Text;

namespace ReDOS;

/// <summary>
/// The persistent virtual machine every ReDOS program shares: one folder on the host that is
/// mounted as drive C:. Programs are imported into C:\PROGRAMS, and whatever they write —
/// save games, config files, generated documents — is still there next time.
/// It is a plain Windows folder, so the user can also manage it with Explorer.
/// </summary>
internal static class Sandbox
{
    /// <summary>Anything larger than this is mounted in place instead of copied in.</summary>
    private const long MaxImportBytes = 1024L * 1024 * 1024;

    internal static string Root => Path.Combine(AppPaths.Root, "sandbox");
    internal static string ProgramsDir => Path.Combine(Root, "PROGRAMS");
    internal static string TempDir => Path.Combine(Root, "TEMP");
    internal static string DocsDir => Path.Combine(Root, "DOCS");
    internal static string DosDir => Path.Combine(Root, "DOS");
    internal static string AutoexecPath => Path.Combine(Root, "AUTOEXEC.BAT");

    internal static bool Exists => Directory.Exists(Root);

    /// <summary>Create the drive-C layout if it is missing. Never overwrites files the user owns.</summary>
    internal static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProgramsDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(DocsDir);
        Directory.CreateDirectory(DosDir);

        if (!File.Exists(AutoexecPath)) WriteText(AutoexecPath, DefaultAutoexec);
        string readme = Path.Combine(Root, "README.TXT");
        if (!File.Exists(readme)) WriteText(readme, ReadmeText);
    }

    private const string DefaultAutoexec = """
        @ECHO OFF
        REM ---------------------------------------------------------------
        REM  AUTOEXEC.BAT - runs every time ReDOS starts a DOS program.
        REM  This is your file: edit it freely, ReDOS never overwrites it.
        REM ---------------------------------------------------------------
        PATH C:\DOS;C:\;Z:\
        SET TEMP=C:\TEMP
        SET TMP=C:\TEMP
        PROMPT $P$G

        """;

    private const string ReadmeText = """
        ReDOS sandbox
        =============

        This folder IS drive C: of your DOS machine. Everything ReDOS runs sees it,
        and every program shares it, so files you create in one program are visible
        in the next one.

          PROGRAMS\   DOS programs that ReDOS has imported, one folder each
          DOCS\       a good place for your own files
          TEMP\       scratch space (TEMP/TMP point here)
          DOS\        put DOS utilities here; it is on the PATH
          AUTOEXEC.BAT  runs before every program - yours to edit

        You can add and delete files here with Explorer, or from the ReDOS manager
        ("ReDOS manager"). Nothing is hidden or encoded.

        Note: the built-in DOS does not process a CONFIG.SYS, so there is no point
        creating one. Device drivers and memory settings are configured through
        ReDOS overrides instead - see the main README.
        """;

    private static void WriteText(string path, string content) =>
        File.WriteAllText(path, content.ReplaceLineEndings("\r\n"), Encoding.ASCII);

    internal static bool Contains(string hostPath)
    {
        string full = Path.GetFullPath(hostPath);
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Translate a host path inside the sandbox into the path DOS sees.</summary>
    internal static string ToDosPath(string hostPath)
    {
        string full = Native.TryGetShortPath(Path.GetFullPath(hostPath)) ?? Path.GetFullPath(hostPath);
        string root = Native.TryGetShortPath(Path.GetFullPath(Root)) ?? Path.GetFullPath(Root);
        root = root.TrimEnd(Path.DirectorySeparatorChar);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;

        string relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar);
        return relative.Length == 0 ? "C:\\" : "C:\\" + relative.ToUpperInvariant();
    }

    internal record ImportResult(string HostPath, string ProgramName, bool WasAlreadyPresent, int SupportFilesCopied);

    /// <summary>
    /// Bring an outside program into the sandbox. The whole containing folder comes along when it
    /// looks like a self-contained program directory; otherwise ReDOS copies the executable plus
    /// whatever data files it can tell the program needs, because DOS programs expect them to be
    /// sitting right next to the executable.
    /// </summary>
    internal static ImportResult Import(string executablePath, string? preferredName = null)
    {
        Ensure();

        string source = Path.GetFullPath(executablePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"There is no file at {source}.", source);

        string sourceDir = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("No containing folder.");
        bool copyFolder = ShouldCopyWholeFolder(sourceDir);

        // Re-importing must land on the existing copy, not pile up FOO, FOO2, FOO3. When the whole
        // folder comes across, the folder is the identity: PLAY.BAT and GAME.EXE next to each other
        // are one program, not two.
        string identity = copyFolder ? sourceDir : source;

        string? name = LookupImportedName(identity);
        if (name is null || !Directory.Exists(Path.Combine(ProgramsDir, name)))
        {
            name = MakeProgramName(preferredName ?? (copyFolder ? Path.GetFileName(sourceDir) : Path.GetFileNameWithoutExtension(source)));
            RecordImportedName(identity, name);
        }

        string destination = Path.Combine(ProgramsDir, name);
        string destinationExe = Path.Combine(destination, Path.GetFileName(source));

        if (File.Exists(destinationExe))
        {
            // Already imported: only look for support files that were missed or added since.
            int added = HarvestSupportFiles(destinationExe, sourceDir, destination);
            return new ImportResult(destinationExe, name, WasAlreadyPresent: true, added);
        }

        Directory.CreateDirectory(destination);
        try
        {
            if (copyFolder)
            {
                CopyTree(sourceDir, destination);
                AppPaths.Log($"imported folder {sourceDir} -> {destination}");
                return new ImportResult(destinationExe, name, WasAlreadyPresent: false, SupportFilesCopied: 0);
            }

            File.Copy(source, destinationExe, overwrite: false);
            int copied = HarvestSupportFiles(destinationExe, sourceDir, destination);
            AppPaths.Log($"imported {source} -> {destinationExe} (+{copied} support files)");
            return new ImportResult(destinationExe, name, WasAlreadyPresent: false, copied);
        }
        catch
        {
            // Do not leave a half-made program behind for the user to clean up.
            try
            {
                if (!Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
            }
            catch (IOException) { /* best effort */ }

            throw;
        }
    }

    /// <summary>
    /// Import a folder of DOS software: work out which executable is the program, and bring the
    /// whole folder along, since its data files live beside it.
    /// </summary>
    internal static ImportResult ImportFolder(string directory)
    {
        Ensure();

        string full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"There is no folder at {full}.");

        string? executable = PickMainExecutable(full, Path.GetFileName(full))
            ?? throw new InvalidOperationException(
                $"No DOS program was found in {Path.GetFileName(full)}. " +
                "If it holds Windows programs rather than DOS ones, ReDOS cannot run them.");

        // The executable names the program better than a folder called "win3_something_shareware".
        return Import(executable, Path.GetFileNameWithoutExtension(executable));
    }

    /// <summary>
    /// Unpack a set of floppy images straight into the sandbox. Many DOS programs were shipped as
    /// plain files on disk, so this often replaces running the installer entirely — and unlike an
    /// installer it works in a terminal session.
    /// </summary>
    internal static ImportResult ImportImages(IReadOnlyList<string> images, string? preferredName = null)
    {
        Ensure();
        if (images.Count == 0) throw new ArgumentException("No images given.", nameof(images));

        var ordered = FloppyImage.SortSet(images);
        string identity = Path.GetFullPath(ordered[0]);

        string? name = LookupImportedName(identity);
        if (name is null || !Directory.Exists(Path.Combine(ProgramsDir, name)))
        {
            name = MakeProgramName(preferredName ?? FloppyImage.SetName(ordered[0]));
            RecordImportedName(identity, name);
        }

        string destination = Path.Combine(ProgramsDir, name);
        bool existed = Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any();
        Directory.CreateDirectory(destination);

        int files = 0;
        foreach (string image in ordered)
        {
            // Disks in a set overlay one another, exactly as they would after a real install.
            files += FloppyImage.Extract(image, destination);
            AppPaths.Log($"extracted {image} -> {destination}");
        }

        return new ImportResult(PickMainExecutable(destination, name) ?? destination, name, existed, files);
    }

    /// <summary>
    /// Copy in the data files the executable needs but that are not there yet. Safe to call on every
    /// launch: it never overwrites a file the program has already written to inside the sandbox.
    /// </summary>
    internal static int HarvestSupportFiles(string sandboxExe, string sourceDir, string destination)
    {
        if (!Directory.Exists(sourceDir)) return 0;

        var present = Directory
            .EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(destination, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = DependencyHarvester.Plan(sandboxExe, sourceDir, present);

        int copied = 0;
        foreach (var (sourceFile, relative) in plan.Files)
        {
            string target = Path.Combine(destination, relative);
            if (File.Exists(target)) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target, overwrite: false);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppPaths.Log($"could not copy support file {sourceFile}: {ex.Message}");
            }
        }

        if (copied > 0) AppPaths.Log($"harvested {copied} support file(s) into {destination}");
        return copied;
    }

    /// <summary>True when copying the folder is safe and sensible — not a drive root, not Downloads.</summary>
    internal static bool ShouldCopyWholeFolder(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Parent is null) return false; // Drive root.

        foreach (Environment.SpecialFolder special in new[]
                 {
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows,
                 })
        {
            string path = Environment.GetFolderPath(special);
            if (path.Length > 0 && string.Equals(path.TrimEnd('\\'), info.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (info.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            long size = 0;
            foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                size += file.Length;
                if (size > MaxImportBytes) return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Maps original host paths to the sandbox folder they were imported into. Kept outside the
    /// sandbox so drive C: stays free of ReDOS bookkeeping files.
    /// </summary>
    private static string ImportIndexPath => Path.Combine(AppPaths.Root, "imports.tsv");

    private static string? LookupImportedName(string source)
    {
        if (!File.Exists(ImportIndexPath)) return null;

        try
        {
            foreach (string line in File.ReadAllLines(ImportIndexPath))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 2 && string.Equals(parts[0], source, StringComparison.OrdinalIgnoreCase))
                    return parts[1];
            }
        }
        catch (IOException)
        {
            // A damaged index just means we import afresh.
        }

        return null;
    }

    private static void RecordImportedName(string source, string name)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(ImportIndexPath, $"{source}\t{name}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Losing the mapping only costs us a duplicate copy later.
        }
    }

    /// <summary>DOS-friendly, unique, 8 characters or fewer.</summary>
    internal static string MakeProgramName(string candidate)
    {
        var builder = new StringBuilder(8);
        foreach (char c in candidate.ToUpperInvariant())
        {
            if (builder.Length == 8) break;
            if (char.IsLetterOrDigit(c) || c is '_' or '-') builder.Append(c);
        }

        string name = builder.Length == 0 ? "PROGRAM" : builder.ToString();
        if (char.IsDigit(name[0])) name = "P" + name[..Math.Min(7, name.Length)];

        string unique = name;
        for (int i = 2; Directory.Exists(Path.Combine(ProgramsDir, unique)) && i < 100; i++)
        {
            string suffix = i.ToString();
            unique = name[..Math.Min(name.Length, 8 - suffix.Length)] + suffix;
        }

        return unique;
    }

    /// <summary>Names that are almost never the program you actually want to launch.</summary>
    private static readonly string[] SecondaryNames =
        ["SETUP", "INSTALL", "INSTAL", "UNINST", "README", "CONFIG", "DOWN", "UPDATE", "PATCH", "DEMO", "TEST"];

    /// <summary>
    /// Guess which executable in a program folder is the program. Extracting a disk set leaves
    /// dozens of binaries lying around, so the alphabetically first one is rarely the right answer.
    /// </summary>
    internal static string? PickMainExecutable(string directory, string programName)
    {
        List<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => DosDetector.IsDosKind(DosDetector.Detect(f)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Count == 0) return null;

        string wanted = programName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        return candidates
            .OrderByDescending(path =>
            {
                string stem = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
                int score = 0;

                if (stem.Equals(programName, StringComparison.OrdinalIgnoreCase)) score += 20;
                else if (wanted.Length > 2 && stem.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) score += 15;
                else if (wanted.Length > 2 && wanted.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) score += 12;

                if (SecondaryNames.Contains(stem)) score -= 10;

                // A program that ships as both .COM and .EXE usually launches from the root folder.
                if (Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (Path.GetExtension(path).Equals(".com", StringComparison.OrdinalIgnoreCase)) score += 1;

                return score;
            })
            .ThenByDescending(path => new FileInfo(path).Length)
            .First();
    }

    internal record InstalledProgram(string Name, string Directory, string? Executable, long SizeBytes, DateTime Modified);

    internal static IReadOnlyList<InstalledProgram> ListPrograms()
    {
        if (!Directory.Exists(ProgramsDir)) return [];

        var result = new List<InstalledProgram>();
        foreach (string dir in Directory.GetDirectories(ProgramsDir))
        {
            var info = new DirectoryInfo(dir);
            long size = 0;
            string? exe = PickMainExecutable(dir, info.Name);
            try
            {
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                    size += file.Length;
            }
            catch (UnauthorizedAccessException)
            {
                // Report what we can rather than dropping the entry.
            }

            result.Add(new InstalledProgram(info.Name, dir, exe, size, info.LastWriteTime));
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static void RemoveProgram(string name)
    {
        string dir = Path.Combine(ProgramsDir, name);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"No program named {name} in the sandbox.");
        Directory.Delete(dir, recursive: true);
        AppPaths.Log($"removed program {name}");
    }

    /// <summary>Wipe the machine back to a fresh install. Destructive, so callers must confirm first.</summary>
    internal static void Reset()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        Ensure();
        AppPaths.Log("sandbox reset");
    }

    internal static void OpenInExplorer(string? subPath = null)
    {
        Ensure();
        string target = subPath is null ? Root : Path.Combine(Root, subPath);
        if (!Directory.Exists(target) && !File.Exists(target)) target = Root;

        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
        {
            UseShellExecute = true,
        });
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, destination));

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), overwrite: true);
    }
}
