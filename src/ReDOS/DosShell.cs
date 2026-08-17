using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReDOS;

/// <summary>
/// The DOS command interpreter the terminal machine sits at.
///
/// The console core emulates the DOS API but provides no shell of its own, so one has to live in
/// the sandbox. ReDOS installs FreeCOM (the FreeDOS COMMAND.COM): it is GPL and freely
/// redistributable, and it targets a modern DOS API. Very old interpreters — MS-DOS 2.0's
/// COMMAND.COM in particular — reach into DOS kernel internals that an emulated API layer does not
/// reproduce, which is why they load but crash on ordinary commands.
/// </summary>
internal static class DosShell
{
    private const string SourceRepo = "FDOS/freecom";
    internal const string ShellFileName = "COMMAND.COM";

    /// <summary>The shell in the sandbox, if one is installed.</summary>
    internal static string? Find()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("REDOS_SHELL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        foreach (string directory in new[] { Sandbox.DosDir, Sandbox.Root })
        {
            string candidate = Path.Combine(directory, ShellFileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Locate the shell, installing FreeCOM into the sandbox on first use.</summary>
    internal static async Task<string> EnsureAsync(Action<string>? progress = null, bool force = false, CancellationToken ct = default)
    {
        Sandbox.Ensure();

        if (!force)
        {
            string? existing = Find();
            if (existing is not null) return existing;
        }

        progress?.Invoke("Looking up a DOS shell...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReDOS");

        var releases = await http.GetFromJsonAsync<JsonElement>(
            $"https://api.github.com/repos/{SourceRepo}/releases", ct);

        string url = PickAsset(releases)
            ?? throw new InvalidOperationException($"No shell package found in the {SourceRepo} releases.");

        progress?.Invoke("Downloading DOS shell...");
        string temp = Path.Combine(Path.GetTempPath(), $"redos-shell-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var netStream = await http.GetStreamAsync(url, ct))
            await using (var fileStream = File.Create(temp))
            {
                await netStream.CopyToAsync(fileStream, ct);
            }

            progress?.Invoke("Installing DOS shell...");
            return InstallFrom(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static string? PickAsset(JsonElement releases)
    {
        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                // "command.zip" is the plain binary package; the rest are translations and sources.
                if ((asset.GetProperty("name").GetString() ?? "").Equals("command.zip", StringComparison.OrdinalIgnoreCase))
                    return asset.GetProperty("browser_download_url").GetString();
            }
        }

        return null;
    }

    /// <summary>Install a shell from a FreeCOM package or a bare COMMAND.COM.</summary>
    internal static string InstallFrom(string path)
    {
        Sandbox.Ensure();
        string destination = Path.Combine(Sandbox.DosDir, ShellFileName);

        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Shell not found.", path);
            File.Copy(path, destination, overwrite: true);
            AppPaths.Log($"dos shell installed from {path}");
            return destination;
        }

        string staging = Path.Combine(Path.GetTempPath(), $"redos-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(path, staging);

            string source = Directory
                .EnumerateFiles(staging, ShellFileName, SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}BIN{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No {ShellFileName} was found inside the archive.");

            File.Copy(source, destination, overwrite: true);

            // The help text is a separate file the shell looks for on the path; bring it along.
            string? help = Directory
                .EnumerateFiles(staging, "COMMAND.E*", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (help is not null)
                File.Copy(help, Path.Combine(Sandbox.DosDir, Path.GetFileName(help)), overwrite: true);

            AppPaths.Log($"dos shell installed: {destination}");
            return destination;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }
}
