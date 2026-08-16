using System.Text;
using System.Text.RegularExpressions;

namespace ReDOS;

/// <summary>
/// Works out which extra files a DOS program needs and brings them into the sandbox with it.
///
/// DOS programs almost never declare their dependencies: they just open GAME.DAT and crash if it
/// is missing. Three cheap signals cover nearly all of it — filenames embedded as literal strings
/// in the executable, files sharing the program's base name, and siblings with well-known DOS data
/// extensions.
/// </summary>
internal static partial class DependencyHarvester
{
    /// <summary>Never drag in more than this much supporting data.</summary>
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>A single file bigger than this is almost certainly not what the program is opening.</summary>
    private const long MaxSingleFileBytes = 128L * 1024 * 1024;

    /// <summary>Extensions DOS programs habitually keep their data in.</summary>
    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dat", ".ovl", ".ovr", ".cfg", ".ini", ".idx", ".res", ".pak", ".grp", ".wad", ".lib",
        ".bin", ".set", ".sav", ".tbl", ".msg", ".lng", ".dic", ".hlp", ".doc", ".txt", ".nfo",
        ".fnt", ".pic", ".pcx", ".lbm", ".gif", ".vga", ".ega", ".cga", ".spr", ".gfx", ".anm",
        ".fli", ".flc", ".snd", ".voc", ".mid", ".mus", ".adl", ".opl", ".rol", ".cmf", ".wav",
        ".drv", ".sys", ".fon", ".chr", ".bgi", ".map", ".lvl", ".scn", ".sc", ".pal",
    };

    /// <summary>Filenames referenced from inside the binary, in 8.3 form, optionally with a relative path.</summary>
    [GeneratedRegex(@"^(?:[A-Z0-9_~!#$%&()\-@^{}]{1,8}\\){0,3}[A-Z0-9_~!#$%&()\-@^{}]{1,8}\.[A-Z0-9]{1,3}$")]
    private static partial Regex DosNamePattern();

    internal sealed record ImportPlan(IReadOnlyList<(string Source, string RelativePath)> Files, long TotalBytes);

    /// <summary>
    /// Decide what to copy alongside <paramref name="executablePath"/>, searching
    /// <paramref name="sourceDir"/> and its subfolders. Paths are returned relative to the
    /// destination folder so any subdirectory layout is preserved.
    /// </summary>
    internal static ImportPlan Plan(string executablePath, string sourceDir, IReadOnlySet<string>? alreadyPresent = null)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        string exeName = Path.GetFileName(executablePath);
        string baseName = Path.GetFileNameWithoutExtension(executablePath);

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new ImportPlan([], 0);
        }

        var referenced = ExtractReferencedNames(executablePath);

        foreach (string candidate in candidates)
        {
            string relative = Path.GetRelativePath(sourceDir, candidate);
            string name = Path.GetFileName(candidate);

            if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (alreadyPresent?.Contains(relative) == true) continue;
            if (chosen.ContainsKey(relative)) continue;

            if (!Wanted(candidate, name, relative, baseName, referenced)) continue;

            long size;
            try
            {
                size = new FileInfo(candidate).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (size > MaxSingleFileBytes) continue;
            if (total + size > MaxTotalBytes) break;

            chosen[relative] = candidate;
            total += size;
        }

        return new ImportPlan(chosen.Select(pair => (pair.Value, pair.Key)).ToList(), total);
    }

    private static bool Wanted(string fullPath, string name, string relative, string baseName, IReadOnlySet<string> referenced)
    {
        // Referenced by name from inside the executable — the strongest signal there is.
        if (referenced.Contains(name) || referenced.Contains(relative.Replace('/', '\\'))) return true;

        // FOO.EXE wants FOO.DAT, FOO.CFG, FOO.OVL...
        if (Path.GetFileNameWithoutExtension(name).Equals(baseName, StringComparison.OrdinalIgnoreCase)) return true;

        string extension = Path.GetExtension(name);

        // A sibling data file in the program's own folder.
        if (DataExtensions.Contains(extension)) return true;

        // Companion executables (installers, setup tools, overlay loaders) sitting next to it.
        return extension is ".com" or ".exe" or ".bat"
               && DosDetector.IsDosKind(DosDetector.Detect(fullPath));
    }

    /// <summary>Pull printable ASCII runs out of the binary and keep the ones shaped like DOS filenames.</summary>
    internal static IReadOnlySet<string> ExtractReferencedNames(string executablePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        byte[] bytes;
        try
        {
            var info = new FileInfo(executablePath);
            if (info.Length > 32L * 1024 * 1024) return names; // Scanning a huge binary is not worth the wait.
            bytes = File.ReadAllBytes(executablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return names;
        }

        var run = new StringBuilder(64);
        foreach (byte b in bytes)
        {
            bool printable = b is >= 0x20 and < 0x7F;
            if (printable && run.Length < 64)
            {
                run.Append((char)b);
                continue;
            }

            Consider(run.ToString(), names);
            run.Clear();
        }

        Consider(run.ToString(), names);
        return names;
    }

    private static void Consider(string text, HashSet<string> names)
    {
        if (text.Length is < 5 or > 64) return;

        string upper = text.Trim().ToUpperInvariant();

        // Strings are often glued together; test the tail after any drive or path prefix too.
        foreach (string piece in new[] { upper, upper.Contains(':') ? upper[(upper.LastIndexOf(':') + 1)..].TrimStart('\\') : upper })
        {
            if (piece.Length is < 5 or > 40) continue;
            if (!DosNamePattern().IsMatch(piece)) continue;

            // Skip the extensions that only ever mean "this is program code we already have".
            string extension = Path.GetExtension(piece);
            if (extension is ".DLL" or ".VXD") continue;

            names.Add(piece);
            names.Add(Path.GetFileName(piece));
        }
    }
}

