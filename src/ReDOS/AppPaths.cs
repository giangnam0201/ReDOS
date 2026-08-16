namespace ReDOS;

internal static class AppPaths
{
    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDOS");

    internal static string Core => Path.Combine(Root, "core");
    internal static string Profiles => Path.Combine(Root, "profiles");
    internal static string Overrides => Path.Combine(Root, "overrides");
    internal static string GlobalOverride => Path.Combine(Overrides, "_global.conf");
    internal static string LogFile => Path.Combine(Root, "redos.log");

    /// <summary>Directory the running ReDOS.exe lives in — where a packaged core sits.</summary>
    internal static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    internal static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(InstallDir, "ReDOS.exe");

    internal static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Overrides);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(LogFile, $"{DateTime.Now:s}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the reason a DOS program fails to start.
        }
    }
}
