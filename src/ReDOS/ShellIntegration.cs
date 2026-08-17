using Microsoft.Win32;

namespace ReDOS;

/// <summary>
/// Registers ReDOS with Explorer. Everything lives under HKEY_CURRENT_USER, so enabling and
/// disabling ReDOS never needs administrator rights and never touches other accounts.
/// </summary>
internal static class ShellIntegration
{
    private const string ProgId = "ReDOS.DosProgram";
    private const string ClassesRoot = @"Software\Classes";
    private const string StateKey = @"Software\ReDOS";
    private const string BackupKey = @"Software\ReDOS\Backup";

    /// <summary>Extensions ReDOS claims outright: on 64-bit Windows nothing else can open them anyway.</summary>
    private static readonly string[] OwnedExtensions = [".com", ".pif", ".dos"];

    /// <summary>Classes that get a right-click "Run with ReDOS" verb without losing their normal behaviour.</summary>
    private static readonly string[] VerbTargets = ["exefile", "batfile", "cmdfile", ProgId];

    internal static bool IsInstalled()
    {
        using var state = Registry.CurrentUser.OpenSubKey(StateKey);
        return state?.GetValue("ExePath") is string path && File.Exists(path);
    }

    internal static bool IsInterceptingExe()
    {
        using var state = Registry.CurrentUser.OpenSubKey(StateKey);
        return state?.GetValue("InterceptExe") is int flag && flag != 0;
    }

    internal static void Install(bool interceptExe, Reporter report)
    {
        string exe = AppPaths.ExecutablePath;
        string runCommand = $"\"{exe}\" run \"%1\" %*";

        using (var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot))
        {
            using (var progId = classes.CreateSubKey(ProgId))
            {
                progId.SetValue("", "MS-DOS Program");
                progId.SetValue("FriendlyTypeName", "MS-DOS Program (ReDOS)");
                using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var command = progId.CreateSubKey(@"shell\open\command")) command.SetValue("", runCommand);
            }

            foreach (string ext in OwnedExtensions)
            {
                using var extKey = classes.CreateSubKey(ext);
                BackupOnce(ext, extKey.GetValue("") as string);
                extKey.SetValue("", ProgId);
            }

            foreach (string target in VerbTargets)
            {
                using var verb = classes.CreateSubKey($@"{target}\shell\ReDOS");
                verb.SetValue("", "Run with ReDOS (MS-DOS)");
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" run --force-dos \"%1\" %*");
            }

            // Makes ReDOS appear in the "Open with" list for any file.
            using (var app = classes.CreateSubKey(@"Applications\ReDOS.exe"))
            {
                app.SetValue("FriendlyAppName", "ReDOS");
                using var command = app.CreateSubKey(@"shell\open\command");
                command.SetValue("", runCommand);
            }

            if (interceptExe)
            {
                using var command = classes.CreateSubKey(@"exefile\shell\open\command");
                command.SetValue("", runCommand);
            }
            else
            {
                RemoveExeInterception(classes);
            }
        }

        // Put the folder ReDOS runs from on PATH, so "redos" works from any shell.
        string installDir = Path.GetDirectoryName(exe) ?? AppPaths.InstallDir;
        try
        {
            if (PathIntegration.Add(installDir))
                report.Status($"Added to your PATH: {installDir}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            report.Status($"Could not update your PATH ({ex.Message}); everything else still works.");
        }

        using (var state = Registry.CurrentUser.CreateSubKey(StateKey))
        {
            state.SetValue("ExePath", exe);
            // Recorded so uninstall removes exactly what was added, even if ReDOS has since moved.
            state.SetValue("PathEntry", installDir);
            state.SetValue("Version", typeof(ShellIntegration).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            state.SetValue("InterceptExe", interceptExe ? 1 : 0, RegistryValueKind.DWord);
        }

        Native.RefreshShellAssociations();
        report.Status("Shell integration installed.");
    }

    internal static void Uninstall(Reporter report)
    {
        using (var state = Registry.CurrentUser.OpenSubKey(StateKey))
        {
            string? recorded = state?.GetValue("PathEntry") as string;
            try
            {
                if (recorded is not null && PathIntegration.Remove(recorded))
                    report.Status($"Removed from your PATH: {recorded}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                report.Status($"Could not update your PATH ({ex.Message}); remove {recorded} by hand if you want it gone.");
            }
        }

        using (var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot))
        {
            foreach (string ext in OwnedExtensions)
            {
                string? original = ReadBackup(ext);
                using var extKey = classes.OpenSubKey(ext, writable: true);
                if (extKey is null) continue;

                if (extKey.GetValue("") as string == ProgId)
                {
                    if (string.IsNullOrEmpty(original)) extKey.SetValue("", "");
                    else extKey.SetValue("", original);
                }
            }

            foreach (string target in VerbTargets)
                DeleteSubKeyTree(classes, $@"{target}\shell\ReDOS");

            DeleteSubKeyTree(classes, ProgId);
            DeleteSubKeyTree(classes, @"Applications\ReDOS.exe");
            RemoveExeInterception(classes);
        }

        DeleteSubKeyTree(Registry.CurrentUser, StateKey);
        Native.RefreshShellAssociations();
        report.Status("Shell integration removed.");
    }

    /// <summary>
    /// Drops the per-user override on the generic .exe verb; Windows then falls back to the
    /// machine-wide default, which is the stock <c>"%1" %*</c>.
    /// </summary>
    private static void RemoveExeInterception(RegistryKey classes) =>
        DeleteSubKeyTree(classes, @"exefile\shell\open");

    private static void BackupOnce(string name, string? value)
    {
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
        if (backup.GetValue(name) is null) backup.SetValue(name, value ?? "");
    }

    private static string? ReadBackup(string name)
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey);
        return backup?.GetValue(name) as string;
    }

    private static void DeleteSubKeyTree(RegistryKey parent, string subKey)
    {
        try { parent.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch (UnauthorizedAccessException) { /* nothing we can do without rights we deliberately do not ask for */ }
    }
}
