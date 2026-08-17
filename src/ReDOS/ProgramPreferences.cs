namespace ReDOS;

/// <summary>
/// Remembers how a particular program wants to be run, so a lesson only has to be learned once.
///
/// Whether a DOS program needs graphics cannot be told from its file: the binaries are packed, and
/// scanning for strings like "VGA" flags text-mode software just as often. So ReDOS finds out the
/// way the user would — by running it — and records the answer.
/// </summary>
internal static class ProgramPreferences
{
    private static string ListPath => Path.Combine(AppPaths.Root, "graphical.txt");

    /// <summary>True when this program has been marked as needing the graphical core.</summary>
    internal static bool PrefersGraphical(string programPath)
    {
        if (!File.Exists(ListPath)) return false;

        try
        {
            string key = Key(programPath);
            return File.ReadLines(ListPath).Any(line => line.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static void SetGraphical(string programPath)
    {
        if (PrefersGraphical(programPath)) return;

        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(ListPath, Key(programPath) + Environment.NewLine);
            AppPaths.Log($"remembered as graphical: {programPath}");
        }
        catch (IOException)
        {
            // Losing the preference only costs one wrong guess next time.
        }
    }

    internal static void ClearGraphical(string programPath)
    {
        if (!File.Exists(ListPath)) return;

        try
        {
            string key = Key(programPath);
            var kept = File.ReadLines(ListPath)
                .Where(line => !line.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            File.WriteAllLines(ListPath, kept);
        }
        catch (IOException)
        {
            // Nothing to do; the preference simply stays.
        }
    }

    /// <summary>
    /// Identify a program by name rather than full path, so the preference survives the program
    /// being re-imported or the sandbox being reset.
    /// </summary>
    private static string Key(string programPath) =>
        Path.GetFileName(programPath).ToLowerInvariant();
}
