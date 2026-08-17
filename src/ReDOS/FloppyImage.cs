using System.Text;
using System.Text.RegularExpressions;

namespace ReDOS;

/// <summary>
/// Reads raw floppy images (.img/.ima/.vfd and friends) directly, so ReDOS can list and extract
/// their contents without booting a DOS machine. Handles the FAT12/FAT16 filesystems every DOS-era
/// floppy uses.
/// </summary>
internal static partial class FloppyImage
{
    /// <summary>Extensions ReDOS treats as a disk image.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".img", ".ima", ".vfd", ".flp", ".dsk", ".360", ".720", ".144",
    };

    internal static bool LooksLikeImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path)) && File.Exists(path);

    internal sealed record Entry(string Name, bool IsDirectory, uint Size, string RelativePath);

    private sealed record Geometry(
        int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int FatCount,
        int RootEntries, int SectorsPerFat, int RootDirOffset, int DataOffset, bool Fat16);

    /// <summary>True when the image carries a filesystem ReDOS can read.</summary>
    internal static bool CanRead(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return TryReadGeometry(bytes) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return false;
        }
    }

    internal static string? VolumeLabel(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var geometry = TryReadGeometry(bytes);
            if (geometry is null) return null;

            foreach (var (raw, offset) in EnumerateDirectory(bytes, geometry, geometry.RootDirOffset, isRoot: true))
            {
                if ((bytes[offset + 11] & 0x08) != 0 && (bytes[offset + 11] & 0x0F) != 0x0F)
                    return Encoding.ASCII.GetString(bytes, offset, 11).Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            // Fall through: no label is not an error.
        }

        return null;
    }

    /// <summary>Copy everything out of the image into <paramref name="destination"/>.</summary>
    internal static int Extract(string imagePath, string destination)
    {
        byte[] bytes = File.ReadAllBytes(imagePath);
        var geometry = TryReadGeometry(bytes)
            ?? throw new InvalidOperationException($"{Path.GetFileName(imagePath)} is not a readable FAT floppy image.");

        Directory.CreateDirectory(destination);
        return ExtractDirectory(bytes, geometry, geometry.RootDirOffset, isRoot: true, destination, depth: 0);
    }

    private static int ExtractDirectory(byte[] bytes, Geometry geometry, int directoryOffset, bool isRoot, string destination, int depth)
    {
        if (depth > 16) return 0; // A malformed image must not send us spiralling.

        int written = 0;
        foreach (var (_, offset) in EnumerateDirectory(bytes, geometry, directoryOffset, isRoot))
        {
            byte attributes = bytes[offset + 11];
            if ((attributes & 0x0F) == 0x0F) continue;   // Long-filename fragment.
            if ((attributes & 0x08) != 0) continue;      // Volume label.

            string name = DecodeName(bytes, offset);
            if (name is "." or "..") continue;

            ushort firstCluster = BitConverter.ToUInt16(bytes, offset + 26);
            uint size = BitConverter.ToUInt32(bytes, offset + 28);

            if ((attributes & 0x10) != 0)
            {
                if (firstCluster < 2) continue;
                string subdirectory = Path.Combine(destination, name);
                Directory.CreateDirectory(subdirectory);
                written += ExtractDirectory(bytes, geometry, ClusterOffset(geometry, firstCluster), isRoot: false, subdirectory, depth + 1);
                continue;
            }

            byte[] contents = ReadFile(bytes, geometry, firstCluster, size);
            File.WriteAllBytes(Path.Combine(destination, name), contents);
            written++;
        }

        return written;
    }

    private static IEnumerable<(int Index, int Offset)> EnumerateDirectory(byte[] bytes, Geometry geometry, int directoryOffset, bool isRoot)
    {
        if (isRoot)
        {
            for (int i = 0; i < geometry.RootEntries; i++)
            {
                int offset = directoryOffset + i * 32;
                if (offset + 32 > bytes.Length) yield break;
                if (bytes[offset] == 0x00) yield break;
                if (bytes[offset] == 0xE5) continue;
                yield return (i, offset);
            }

            yield break;
        }

        // A subdirectory is a normal cluster chain; walk it one entry at a time.
        int clusterBytes = geometry.SectorsPerCluster * geometry.BytesPerSector;
        int index = 0;
        for (int position = directoryOffset; position + 32 <= bytes.Length; position += 32, index++)
        {
            if (bytes[position] == 0x00) yield break;
            if (bytes[position] != 0xE5) yield return (index, position);

            // Stop at the end of this cluster; chained subdirectories beyond it are rare on floppies.
            if ((index + 1) * 32 >= clusterBytes) yield break;
        }
    }

    private static string DecodeName(byte[] bytes, int offset)
    {
        string stem = Encoding.ASCII.GetString(bytes, offset, 8).TrimEnd();
        string extension = Encoding.ASCII.GetString(bytes, offset + 8, 3).TrimEnd();

        // 0x05 stands in for a leading 0xE5 in the on-disk name.
        if (stem.Length > 0 && bytes[offset] == 0x05) stem = "å" + stem[1..];

        string name = extension.Length > 0 ? $"{stem}.{extension}" : stem;
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    private static byte[] ReadFile(byte[] bytes, Geometry geometry, ushort firstCluster, uint size)
    {
        var output = new MemoryStream((int)Math.Min(size, int.MaxValue));
        int clusterBytes = geometry.SectorsPerCluster * geometry.BytesPerSector;
        uint remaining = size;
        int cluster = firstCluster;
        var visited = new HashSet<int>();

        while (cluster >= 2 && remaining > 0 && visited.Add(cluster))
        {
            int offset = ClusterOffset(geometry, cluster);
            if (offset < 0 || offset >= bytes.Length) break;

            int take = (int)Math.Min(remaining, (uint)Math.Min(clusterBytes, bytes.Length - offset));
            output.Write(bytes, offset, take);
            remaining -= (uint)take;

            cluster = NextCluster(bytes, geometry, cluster);
            if (geometry.Fat16 ? cluster >= 0xFFF8 : cluster >= 0xFF8) break;
        }

        return output.ToArray();
    }

    private static int ClusterOffset(Geometry geometry, int cluster) =>
        geometry.DataOffset + (cluster - 2) * geometry.SectorsPerCluster * geometry.BytesPerSector;

    private static int NextCluster(byte[] bytes, Geometry geometry, int cluster)
    {
        int fatOffset = geometry.ReservedSectors * geometry.BytesPerSector;

        if (geometry.Fat16)
        {
            int position = fatOffset + cluster * 2;
            return position + 1 < bytes.Length ? BitConverter.ToUInt16(bytes, position) : 0xFFFF;
        }

        // FAT12 packs two entries into three bytes.
        int index = fatOffset + cluster + (cluster / 2);
        if (index + 1 >= bytes.Length) return 0xFFF;

        int value = bytes[index] | (bytes[index + 1] << 8);
        return (cluster & 1) == 0 ? value & 0x0FFF : value >> 4;
    }

    private static Geometry? TryReadGeometry(byte[] bytes)
    {
        if (bytes.Length < 512) return null;

        int bytesPerSector = BitConverter.ToUInt16(bytes, 11);
        int sectorsPerCluster = bytes[13];
        int reserved = BitConverter.ToUInt16(bytes, 14);
        int fatCount = bytes[16];
        int rootEntries = BitConverter.ToUInt16(bytes, 17);
        int sectorsPerFat = BitConverter.ToUInt16(bytes, 22);

        // Sanity-check the BPB: a non-filesystem image will fail at least one of these.
        if (bytesPerSector is not (128 or 256 or 512 or 1024 or 2048 or 4096)) return null;
        if (sectorsPerCluster is < 1 or > 128 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0) return null;
        if (reserved < 1 || fatCount is < 1 or > 4 || rootEntries < 1 || sectorsPerFat < 1) return null;

        int rootDirOffset = (reserved + fatCount * sectorsPerFat) * bytesPerSector;
        int rootDirBytes = rootEntries * 32;
        int dataOffset = rootDirOffset + rootDirBytes;
        if (dataOffset >= bytes.Length) return null;

        int totalSectors = BitConverter.ToUInt16(bytes, 19);
        if (totalSectors == 0) totalSectors = (int)Math.Min(int.MaxValue, BitConverter.ToUInt32(bytes, 32));
        int clusters = sectorsPerCluster > 0 ? (totalSectors - dataOffset / bytesPerSector) / sectorsPerCluster : 0;

        return new Geometry(bytesPerSector, sectorsPerCluster, reserved, fatCount, rootEntries,
            sectorsPerFat, rootDirOffset, dataOffset, Fat16: clusters >= 4085);
    }

    /// <summary>Strips a trailing "disk 2", "(disk 2 of 3)" and the like, so a set shares one name.</summary>
    [GeneratedRegex(@"[\s_\-\(\[]*(disk|disc|dsk|floppy)[\s_\-#]*\d+(\s*of\s*\d+)?[\s_\-\)\]]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiskSuffixPattern();

    internal static string SetName(string imagePath)
    {
        string name = Path.GetFileNameWithoutExtension(imagePath);
        string trimmed = DiskSuffixPattern().Replace(name, "").Trim();
        return trimmed.Length > 0 ? trimmed : name;
    }

    /// <summary>
    /// Order images the way their labels read, not the way the shell sorted them. Titles group
    /// first, so a "Word 4 Learn" disk set never interleaves with the "Word 4" one.
    /// </summary>
    internal static IReadOnlyList<string> SortSet(IEnumerable<string> images) =>
        images
            .OrderBy(SetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(image =>
            {
                var match = Regex.Match(Path.GetFileNameWithoutExtension(image), @"(\d+)\s*$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int trailing)) return trailing;

                match = Regex.Match(Path.GetFileNameWithoutExtension(image), @"(?:disk|disc|dsk)[\s_\-#]*(\d+)", RegexOptions.IgnoreCase);
                return match.Success && int.TryParse(match.Groups[1].Value, out int numbered) ? numbered : int.MaxValue;
            })
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
