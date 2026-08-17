using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReDOS;

/// <summary>
/// Finds (and if necessary fetches) the DOS execution core. Release builds ship it inside
/// <c>core\</c> next to ReDOS.exe; a bare exe downloads it once into %LOCALAPPDATA%.
/// </summary>
internal static class CoreProvider
{
    private const string CoreRepo = "joncampbell123/dosbox-x";
    internal const string CoreExeName = "dosbox-x.exe";

    /// <summary>
    /// The graphical core gets its own folder: it ships as a tree of support files that has to be
    /// replaced wholesale on update, and it must not take the console core down with it.
    /// </summary>
    private static string LocalDir => Path.Combine(AppPaths.Core, "dosbox-x");

    /// <summary>Locate the core without touching the network. Null when nothing is installed yet.</summary>
    internal static string? Find()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("REDOS_CORE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (File.Exists(fromEnv)) return fromEnv;
            string candidate = Path.Combine(fromEnv, CoreExeName);
            if (File.Exists(candidate)) return candidate;
        }

        string packaged = Path.Combine(AppPaths.InstallDir, "core", CoreExeName);
        if (File.Exists(packaged)) return packaged;

        string local = Path.Combine(LocalDir, CoreExeName);
        if (File.Exists(local)) return local;

        // Installs made before the core folders were split.
        string legacy = Path.Combine(AppPaths.Core, CoreExeName);
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>Find the core, downloading it on first use. <paramref name="progress"/> receives status text.</summary>
    internal static async Task<string> EnsureAsync(Action<string>? progress = null, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            string? existing = Find();
            if (existing is not null) return existing;
        }

        progress?.Invoke("Looking up the latest DOS core...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReDOS");

        var release = await http.GetFromJsonAsync<JsonElement>(
            $"https://api.github.com/repos/{CoreRepo}/releases/latest", ct);

        string? url = PickAsset(release);
        if (url is null)
            throw new InvalidOperationException("No suitable Windows x64 build found in the latest DOSBox-X release.");

        progress?.Invoke("Downloading DOS core...");
        string temp = Path.Combine(Path.GetTempPath(), $"redos-core-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var netStream = await http.GetStreamAsync(url, ct))
            await using (var fileStream = File.Create(temp))
            {
                await netStream.CopyToAsync(fileStream, ct);
            }

            progress?.Invoke("Installing DOS core...");
            return Install(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static string? PickAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;

        string? best = null;
        int bestScore = int.MinValue;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            string lower = name.ToLowerInvariant();
            if (lower.Contains("arm")) continue;
            if (lower.Contains("macos") || lower.Contains("linux")) continue;

            // Prefer the MSVC 64-bit build; the MinGW one is the fallback.
            int score = 0;
            if (lower.Contains("win64") || lower.Contains("x64")) score += 10;
            if (lower.Contains("vsbuild") || lower.Contains("msvc")) score += 5;
            else if (lower.Contains("mingw")) score += 3;
            if (lower.Contains("win32") || lower.Contains("x86")) score -= 8;
            if (lower.Contains("lowend") || lower.Contains("xp")) score -= 6;

            if (score > bestScore)
            {
                bestScore = score;
                best = asset.GetProperty("browser_download_url").GetString();
            }
        }

        return best;
    }

    /// <summary>Extract a DOSBox-X distribution zip into the local core directory.</summary>
    internal static string Install(string zipPath)
    {
        string staging = Path.Combine(Path.GetTempPath(), $"redos-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);

            string? exe = LocateCoreExe(staging)
                ?? throw new InvalidOperationException($"{CoreExeName} was not found inside the downloaded archive.");

            string sourceDir = Path.GetDirectoryName(exe)!;
            if (Directory.Exists(LocalDir)) Directory.Delete(LocalDir, recursive: true);
            CopyTree(sourceDir, LocalDir);

            return Path.Combine(LocalDir, CoreExeName);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Distribution zips carry several builds; pick the 64-bit one.</summary>
    internal static string? LocateCoreExe(string root)
    {
        var candidates = Directory.EnumerateFiles(root, CoreExeName, SearchOption.AllDirectories).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(p =>
            {
                string lower = p.ToLowerInvariant();
                int score = 0;
                if (lower.Contains("x64") || lower.Contains("win64") || lower.Contains("64bit")) score += 10;
                if (lower.Contains("x86") || lower.Contains("win32")) score -= 10;
                if (lower.Contains("debug")) score -= 5;
                return score;
            })
            .ThenByDescending(p => new FileInfo(p).Length)
            .First();
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
