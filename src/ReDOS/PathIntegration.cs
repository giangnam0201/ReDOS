using Microsoft.Win32;

namespace ReDOS;

/// <summary>
/// Puts ReDOS on the user's PATH so <c>redos</c> works from any shell, without touching the
/// machine-wide PATH or needing administrator rights.
/// </summary>
internal static class PathIntegration
{
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";

    internal static bool Contains(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        return Split(ReadRaw(key)).Any(entry => SamePath(entry, directory));
    }

    /// <summary>Append <paramref name="directory"/> to the user PATH. Returns false if it was already there.</summary>
    internal static bool Add(string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);
        string raw = ReadRaw(key);

        var entries = Split(raw).ToList();
        if (entries.Any(entry => SamePath(entry, directory))) return false;

        entries.Add(directory.TrimEnd(Path.DirectorySeparatorChar));
        Write(key, raw, string.Join(';', entries));

        // So this process and anything it starts can use it immediately.
        Environment.SetEnvironmentVariable("PATH",
            Environment.GetEnvironmentVariable("PATH") + ";" + directory);

        Native.BroadcastEnvironmentChange();
        AppPaths.Log($"added to user PATH: {directory}");
        return true;
    }

    /// <summary>Remove <paramref name="directory"/> from the user PATH. Returns false if it was not there.</summary>
    internal static bool Remove(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: true);
        if (key is null) return false;

        string raw = ReadRaw(key);
        var entries = Split(raw).ToList();

        int removed = entries.RemoveAll(entry => SamePath(entry, directory));
        if (removed == 0) return false;

        Write(key, raw, string.Join(';', entries));
        Native.BroadcastEnvironmentChange();
        AppPaths.Log($"removed from user PATH: {directory}");
        return true;
    }

    /// <summary>
    /// Read PATH without expanding it. Expanding would turn entries like %USERPROFILE%\bin into
    /// fixed paths the moment we wrote the value back.
    /// </summary>
    private static string ReadRaw(RegistryKey? key) =>
        key?.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    /// <summary>Write PATH back, keeping whatever type it already had.</summary>
    private static void Write(RegistryKey key, string previousRaw, string value)
    {
        RegistryValueKind kind = key.GetValueKind(PathValue) is RegistryValueKind.ExpandString
            || previousRaw.Contains('%')
                ? RegistryValueKind.ExpandString
                : RegistryValueKind.String;

        key.SetValue(PathValue, value, kind);
    }

    private static IEnumerable<string> Split(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(b)).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed PATH entry can never match a real directory.
            return false;
        }
    }
}
