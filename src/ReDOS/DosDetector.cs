namespace ReDOS;

internal enum ProgramKind
{
    /// <summary>Path does not exist or is unreadable.</summary>
    Missing,
    /// <summary>Plain real-mode MZ executable, or a .COM flat binary. NTVDM territory.</summary>
    Dos,
    /// <summary>MZ stub in front of an LE/LX extender payload (DOS/4GW, PMODE/W, CauseWay...). Still DOS.</summary>
    DosExtended,
    /// <summary>NE — 16-bit Windows or OS/2. Not something ReDOS runs.</summary>
    Sixteen,
    /// <summary>PE — a normal modern Windows binary. ReDOS steps out of the way.</summary>
    Native,
    /// <summary>Batch file: only treated as DOS when the user explicitly asks for it.</summary>
    Batch,
    Unknown,
}

internal static class DosDetector
{
    /// <summary>Largest a real .COM can be: one segment minus the PSP.</summary>
    private const long MaxComSize = 65280;

    internal static bool IsDosKind(ProgramKind kind) => kind is ProgramKind.Dos or ProgramKind.DosExtended;

    internal static ProgramKind Detect(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return ProgramKind.Missing;
        }
        catch
        {
            return ProgramKind.Missing;
        }

        string ext = info.Extension.ToLowerInvariant();
        if (ext is ".bat" or ".cmd") return ProgramKind.Batch;
        if (ext is ".pif" or ".dos") return ProgramKind.Dos;

        byte[] head;
        try
        {
            using var stream = File.OpenRead(path);
            head = new byte[0x40];
            int read = stream.Read(head, 0, head.Length);
            if (read < 2)
            {
                // Too small to carry any header — a stub .COM is still legitimate.
                return ext == ".com" && info.Length > 0 ? ProgramKind.Dos : ProgramKind.Unknown;
            }

            if (head[0] != (byte)'M' || head[1] != (byte)'Z')
            {
                // No MZ. A .COM is a headerless flat binary, so size is the only sanity check we get.
                return ext == ".com" && info.Length is > 0 and <= MaxComSize ? ProgramKind.Dos : ProgramKind.Unknown;
            }

            if (read < 0x40) return ProgramKind.Dos; // MZ header truncated: nothing can follow it.

            uint lfanew = BitConverter.ToUInt32(head, 0x3C);
            if (lfanew < 0x40 || lfanew > info.Length - 2) return ProgramKind.Dos;

            stream.Seek(lfanew, SeekOrigin.Begin);
            Span<byte> sig = stackalloc byte[2];
            if (stream.Read(sig) < 2) return ProgramKind.Dos;

            return (char)sig[0] switch
            {
                'P' when sig[1] == 'E' => ProgramKind.Native,
                'N' when sig[1] == 'E' => ProgramKind.Sixteen,
                'L' when sig[1] is (byte)'E' or (byte)'X' => ProgramKind.DosExtended,
                _ => ProgramKind.Dos,
            };
        }
        catch (IOException)
        {
            return ProgramKind.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return ProgramKind.Missing;
        }
    }

    internal static string Describe(ProgramKind kind) => kind switch
    {
        ProgramKind.Dos => "MS-DOS real-mode executable",
        ProgramKind.DosExtended => "MS-DOS protected-mode executable (DOS extender)",
        ProgramKind.Sixteen => "16-bit Windows/OS2 (NE) executable",
        ProgramKind.Native => "native Windows (PE) executable",
        ProgramKind.Batch => "batch file",
        ProgramKind.Missing => "missing or unreadable file",
        _ => "unrecognised file",
    };
}
