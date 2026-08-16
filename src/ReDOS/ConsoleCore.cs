using System.IO.Compression;

namespace ReDOS;

/// <summary>
/// The console-hosted DOS core. Unlike the graphical core it writes to the parent console's
/// stdin/stdout, so a DOS session can live inside cmd, PowerShell or a Windows Terminal tab.
/// It only handles text-mode programs — anything that touches video memory needs the graphical core.
/// </summary>
internal static class ConsoleCore
{
    internal const string CoreExeName = "msdos-player.exe";

    /// <summary>Locate the console core without touching the network. Null when it is not installed.</summary>
    internal static string? Find()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("REDOS_CONSOLE_CORE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (File.Exists(fromEnv)) return fromEnv;
            string candidate = Path.Combine(fromEnv, CoreExeName);
            if (File.Exists(candidate)) return candidate;
        }

        string packaged = Path.Combine(AppPaths.InstallDir, "core", CoreExeName);
        if (File.Exists(packaged)) return packaged;

        string local = Path.Combine(AppPaths.Core, CoreExeName);
        if (File.Exists(local)) return local;

        return null;
    }

    internal static bool IsAvailable => Find() is not null;

    /// <summary>
    /// Register a console core the user supplied, from either a zip or a bare exe. There is no
    /// canonical download for this one, so ReDOS never fetches it behind the user's back.
    /// </summary>
    internal static string InstallFrom(string path)
    {
        Directory.CreateDirectory(AppPaths.Core);
        string destination = Path.Combine(AppPaths.Core, CoreExeName);

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string staging = Path.Combine(Path.GetTempPath(), $"redos-console-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                ZipFile.ExtractToDirectory(path, staging);
                string source = Directory
                    .EnumerateFiles(staging, "*.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => Path.GetFileName(p).Equals(CoreExeName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(p => p.Contains("64", StringComparison.Ordinal))
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("No executable was found inside the archive.");

                File.Copy(source, destination, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }
        else
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Console core not found.", path);
            File.Copy(path, destination, overwrite: true);
        }

        AppPaths.Log($"console core installed: {destination}");
        return destination;
    }

    /// <summary>
    /// Build the command that starts a console DOS session. The core takes over the current console,
    /// so whatever terminal hosts this process becomes the DOS machine.
    /// </summary>
    internal static (string Exe, List<string> Args)? BuildCommand(string? programPath, IReadOnlyList<string> programArgs)
    {
        string? core = Find();
        if (core is null) return null;

        var args = new List<string>();
        if (programPath is null)
        {
            // No program: hand the user COMMAND.COM itself.
            args.Add("command.com");
        }
        else
        {
            args.Add(Native.TryGetShortPath(programPath) ?? programPath);
            args.AddRange(programArgs);
        }

        return (core, args);
    }
}
