using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

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
    /// MS-DOS Player states no redistribution terms, so ReDOS does not ship it inside the release
    /// archive. It is fetched to the user's own machine on first use instead, which needs no
    /// decision from them and redistributes nothing.
    /// </summary>
    private const string SourceRepo = "roytam1/msdos-player";

    /// <summary>
    /// The CPU the emulated machine presents. 486 runs everything a text-mode DOS program is likely
    /// to use, including 32-bit instructions, without the slowdown of the later cores.
    /// </summary>
    private const string PreferredBuild = "Release_i486-x64";

    /// <summary>Locate the console core, downloading it on first use.</summary>
    internal static async Task<string> EnsureAsync(Action<string>? progress = null, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            string? existing = Find();
            if (existing is not null) return existing;
        }

        progress?.Invoke("Looking up the console DOS core...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReDOS");

        var releases = await http.GetFromJsonAsync<JsonElement>(
            $"https://api.github.com/repos/{SourceRepo}/releases", ct);

        string? url = PickAsset(releases)
            ?? throw new InvalidOperationException($"No console core archive found in the {SourceRepo} releases.");

        progress?.Invoke("Downloading console DOS core...");
        string temp = Path.Combine(Path.GetTempPath(), $"redos-console-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var netStream = await http.GetStreamAsync(url, ct))
            await using (var fileStream = File.Create(temp))
            {
                await netStream.CopyToAsync(fileStream, ct);
            }

            progress?.Invoke("Installing console DOS core...");
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

            string? best = null;
            foreach (var asset in assets.EnumerateArray())
            {
                string name = (asset.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                if (!name.EndsWith(".zip")) continue;

                // The newer toolchain build first; the older one is a usable fallback.
                if (name.Contains("vc13")) return asset.GetProperty("browser_download_url").GetString();
                best ??= asset.GetProperty("browser_download_url").GetString();
            }

            if (best is not null) return best;
        }

        return null;
    }

    /// <summary>Register a console core from either a distribution zip or a bare exe.</summary>
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
                string source = SelectBuild(staging)
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
    /// The archive ships one build per emulated CPU. Pick the 64-bit host build of the CPU we want,
    /// then fall back through anything 64-bit rather than failing on a layout change.
    /// </summary>
    private static string? SelectBuild(string root)
    {
        var candidates = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(p => p.Contains(PreferredBuild, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Contains("-x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Contains("i386", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Contains("Win32", StringComparison.OrdinalIgnoreCase))
            .First();
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
