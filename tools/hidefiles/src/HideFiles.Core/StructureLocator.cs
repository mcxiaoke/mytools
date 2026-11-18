using System.Buffers.Binary;
using System.Text;

namespace HideFiles.Core;

/// <summary>
/// 格式关键结构定位器：找出"解码必需"的小块区域，供结构锁模式原地加密。
/// 纯 BCL 实现，无第三方依赖；无法识别或解析失败时返回空清单（结构加密模式直接报错拒绝处理，不自动降级）。
///
/// MP4: 递归 box 树，锁定 moov 下 stco/co64/stsc/stsz/stss/avcC/hvcC 的载荷
/// MKV: EBML VINT 解析，锁定 CodecPrivate / Cues 载荷 + SeekHead
/// JPG: 锁定 SOI 之后到 SOS 之前的参数段（DQT/DHT/SOF/APPn）
/// PNG: 锁定 IHDR 数据块 + 首个 IDAT 数据块头部
/// </summary>
public static class StructureLocator
{
    /// <summary>结构混淆支持的扩展名（与 <see cref="Locate"/> 的分支保持一致）</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov",
        ".mkv", ".webm",
        ".jpg", ".jpeg",
        ".png"
    };

    /// <summary>
    /// 该扩展名能否走结构混淆（只判断格式类别，不代表文件一定解析成功）。
    /// 用于界面预判"不支持"，避免把格式不支持误报成处理失败。
    /// </summary>
    public static bool IsSupportedExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);

    public static List<LockRegion> Locate(string path, string ext)
    {
        try
        {
            return ext.ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" or ".mov" => LocateMp4(path),
                ".mkv" or ".webm" => LocateMkv(path),
                ".jpg" or ".jpeg" => LocateJpg(path),
                ".png" => LocatePng(path),
                _ => new List<LockRegion>()
            };
        }
        catch
        {
            return new List<LockRegion>();
        }
    }

    // ==================== MP4 ====================
    private static readonly HashSet<string> Mp4Containers =
        new(StringComparer.Ordinal) { "moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "mvex" };
    private static readonly HashSet<string> Mp4Targets =
        new(StringComparer.Ordinal) { "stco", "co64", "stsc", "stsz", "stss", "avcC", "hvcC", "esds" };

    private static List<LockRegion> LocateMp4(string path)
    {
        var regions = new List<LockRegion>();
        using var fs = File.OpenRead(path);
        long end = fs.Length;
        WalkMp4(fs, 0, end, regions, depth: 0);
        return regions;
    }

    private static void WalkMp4(FileStream fs, long start, long end, List<LockRegion> regions, int depth)
    {
        if (depth > 6) return;
        long p = start;
        byte[] hdr = new byte[16];
        while (p + 8 <= end)
        {
            fs.Seek(p, SeekOrigin.Begin);
            fs.ReadExactly(hdr, 0, 8);
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
            string type = Encoding.Latin1.GetString(hdr, 4, 4);
            int h = 8;
            long size;
            if (size32 == 1)
            {
                fs.ReadExactly(hdr, 0, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0, 8));
                h = 16;
            }
            else if (size32 == 0) size = end - p;
            else size = size32;

            if (size < h || p + size > end) break;

            if (Mp4Targets.Contains(type) && size > h)
            {
                // 锁定 box 载荷（跳过 header），单块上限 1MB 防止异常 box 拖慢
                int len = (int)Math.Min(size - h, 1024 * 1024);
                regions.Add(new LockRegion(p + h, len, type));
            }

            if (Mp4Containers.Contains(type))
                WalkMp4(fs, p + h, p + size, regions, depth + 1);

            p += size;
        }
    }

    // ==================== MKV / WebM (EBML) ====================
    private static readonly Dictionary<string, string> MkvNames = new()
    {
        ["1A45DFA3"] = "EBML", ["18538067"] = "Segment", ["1549A966"] = "Info",
        ["1654AE6B"] = "Tracks", ["1C53BB6B"] = "Cues", ["1F43B675"] = "Cluster",
        ["63A2"] = "CodecPrivate", ["114D9B74"] = "SeekHead", ["AE"] = "TrackEntry",
        ["E0"] = "Video", ["E1"] = "Audio", ["1254C367"] = "Tags"
    };
    private static readonly HashSet<string> MkvContainers =
        new(StringComparer.Ordinal) { "EBML", "Segment", "Tracks", "TrackEntry", "Video", "Audio", "Info", "Cues", "SeekHead" };
    private static readonly HashSet<string> MkvTargets =
        new(StringComparer.Ordinal) { "CodecPrivate", "Cues", "SeekHead" };

    private static List<LockRegion> LocateMkv(string path)
    {
        var regions = new List<LockRegion>();
        using var fs = File.OpenRead(path);

        // 关键结构大多在文件前部，但 Cues/SeekHead 常被写在文件尾部
        const int Window = 8 * 1024 * 1024;
        var windows = new List<(long BaseOffset, byte[] Buffer)>();

        int headLen = (int)Math.Min(fs.Length, Window);
        byte[] head = new byte[headLen];
        fs.ReadExactly(head, 0, headLen);
        windows.Add((0, head));

        if (fs.Length > headLen)
        {
            int tailLen = (int)Math.Min(fs.Length - headLen, Window);
            byte[] tail = new byte[tailLen];
            fs.Seek(fs.Length - tailLen, SeekOrigin.Begin);
            fs.ReadExactly(tail, 0, tailLen);
            windows.Add((fs.Length - tailLen, tail));
        }

        foreach (var (baseOffset, buffer) in windows)
            WalkEbml(buffer, baseOffset, 0, buffer.Length, regions, depth: 0);

        // 尾部窗口再按元素签名扫一次：Cues/SeekHead 常写在文件末尾，那里未必正好是元素边界
        if (windows.Count > 1)
        {
            var (tailBase, tailBuf) = windows[1];
            ScanElementById(tailBuf, tailBase, CuesId, "Cues", regions);
            ScanElementById(tailBuf, tailBase, SeekHeadId, "SeekHead", regions);
        }

        return regions;
    }

    private static readonly byte[] CuesId = { 0x1C, 0x53, 0xBB, 0x6B };
    private static readonly byte[] SeekHeadId = { 0x11, 0x4D, 0x9B, 0x74 };

    /// <summary>
    /// 在缓冲区里按元素签名定位（用于文件尾部窗口：那里通常不是从元素边界开始解析的）。
    /// </summary>
    private static void ScanElementById(byte[] buf, long baseOffset, byte[] id, string name, List<LockRegion> regions)
    {
        if (regions.Any(r => r.Kind == name)) return;   // 前面的窗口已定位到

        for (int i = 0; i + id.Length + 1 < buf.Length; i++)
        {
            if (buf[i] != id[0]) continue;

            bool matched = true;
            for (int k = 1; k < id.Length; k++)
            {
                if (buf[i + k] != id[k]) { matched = false; break; }
            }
            if (!matched) continue;

            int sizePos = i + id.Length;
            int sizeLen = VintSizeLen(buf[sizePos]);
            if (sizeLen < 1 || sizePos + sizeLen > buf.Length) continue;

            ulong size = ReadVintSize(buf, sizePos, sizeLen, out bool unknown);
            if (unknown || size == 0) continue;

            int dataStart = sizePos + sizeLen;
            if (dataStart >= buf.Length) continue;

            int len = (int)Math.Min((ulong)(buf.Length - dataStart), Math.Min(size, 256 * 1024));
            if (len <= 0) continue;

            regions.Add(new LockRegion(baseOffset + dataStart, len, name));
            return;
        }
    }

    private static void WalkEbml(byte[] buf, long baseOffset, long start, long end, List<LockRegion> regions, int depth)
    {
        if (depth > 8) return;
        long p = start;
        while (p + 2 <= end && p + 2 <= buf.Length)
        {
            int idLen = VintIdLen(buf[(int)p]);
            if (idLen < 1 || p + idLen + 1 > buf.Length) break;
            string id = Convert.ToHexString(buf.AsSpan((int)p, idLen));
            int sp = (int)(p + idLen);
            int sizeLen = VintSizeLen(buf[sp]);
            if (sizeLen < 1 || sp + sizeLen > buf.Length) break;
            ulong sizeRaw = ReadVintSize(buf, sp, sizeLen, out bool unknown);
            long dataStart = sp + sizeLen;
            long dataEnd = unknown ? end : dataStart + (long)sizeRaw;
            string name = MkvNames.TryGetValue(id, out var n) ? n : id;

            if (MkvTargets.Contains(name) && dataEnd > dataStart && sizeRaw <= 4 * 1024 * 1024)
            {
                int len = (int)Math.Min(dataEnd - dataStart, 256 * 1024);
                if (dataStart + len <= buf.Length && !regions.Any(r => r.Kind == name))
                    regions.Add(new LockRegion(baseOffset + dataStart, len, name));
            }

            // Segment 等 master 可能声明超大/未知长度，用已读缓冲区末尾作为边界继续深入
            if (MkvContainers.Contains(name) && dataEnd > dataStart)
            {
                long childEnd = Math.Min(dataEnd, buf.Length);
                if (childEnd > dataStart)
                    WalkEbml(buf, baseOffset, dataStart, childEnd, regions, depth + 1);
            }
            if (unknown || dataEnd <= p) break;
            p = dataEnd;
        }
    }

    private static int VintIdLen(byte b)
    {
        if ((b & 0x80) != 0) return 1;
        if ((b & 0x40) != 0) return 2;
        if ((b & 0x20) != 0) return 3;
        if ((b & 0x10) != 0) return 4;
        if ((b & 0x08) != 0) return 5;
        return 0;
    }

    private static int VintSizeLen(byte b)
    {
        for (int n = 1; n <= 8; n++)
            if ((b & (0x80 >> (n - 1))) != 0) return n;
        return 0;
    }

    private static ulong ReadVintSize(byte[] buf, int pos, int len, out bool unknown)
    {
        ulong v = (ulong)(buf[pos] & (0xFF >> len));
        for (int i = 1; i < len; i++) v = (v << 8) | buf[pos + i];
        unknown = v == (1UL << (7 * len)) - 1;
        return v;
    }

    // ==================== JPEG ====================
    private static List<LockRegion> LocateJpg(string path)
    {
        var regions = new List<LockRegion>();
        using var fs = File.OpenRead(path);
        int readLen = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
        byte[] buf = new byte[readLen];
        fs.ReadExactly(buf, 0, readLen);

        // 按段长正规遍历 SOI(FFD8) 之后的各段，直到 SOS(FFDA)。
        // 不能逐字节搜 FFDA：EXIF 缩略图内部就含有该字节对，会把锁定区提前截断。
        int sos = -1;
        int p = 2;
        while (p + 4 <= buf.Length)
        {
            if (buf[p] != 0xFF) { p++; continue; }   // 容错：跳过段间填充字节
            byte marker = buf[p + 1];

            if (marker == 0xDA) { sos = p; break; }                                     // SOS
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))  // 无长度字段的标记
            {
                p += 2;
                continue;
            }

            int segLen = (buf[p + 2] << 8) | buf[p + 3];
            if (segLen < 2) break;
            p += 2 + segLen;
        }

        if (sos > 2)
        {
            int len = Math.Min(sos - 2, 64 * 1024);
            regions.Add(new LockRegion(2, len, "JPEG-PARAMS"));
        }
        return regions;
    }

    // ==================== PNG ====================
    private static List<LockRegion> LocatePng(string path)
    {
        var regions = new List<LockRegion>();
        using var fs = File.OpenRead(path);
        // PNG signature 8 字节后是 chunk: len(4)+type(4)+data+crc(4)
        byte[] head = new byte[64];
        fs.ReadExactly(head, 0, head.Length);
        // IHDR: data at 16, len 13
        if (Encoding.ASCII.GetString(head, 12, 4) == "IHDR")
            regions.Add(new LockRegion(16, 13, "IHDR"));
        // 定位首个 IDAT：读前 256KB 找
        int scanLen = (int)Math.Min(fs.Length, 256 * 1024);
        byte[] scan = new byte[scanLen];
        fs.Seek(0, SeekOrigin.Begin);
        fs.ReadExactly(scan, 0, scanLen);
        for (int p = 8; p + 8 <= scan.Length; )
        {
            int clen = BinaryPrimitives.ReadInt32BigEndian(scan.AsSpan(p, 4));
            if (clen < 0) break;
            string type = Encoding.ASCII.GetString(scan, p + 4, 4);
            if (type == "IDAT")
            {
                // 只锁定 IDAT 数据头部（zlib 头 + 前若干字节）。
                // 注意不要用整个 chunk 长度判断扫描窗口——大图的单个 IDAT 常见几百 KB 到数 MB，
                // 那样会直接放弃定位，使结构锁退化成"只加密文件头"。
                int available = Math.Min(clen, 4096);
                available = Math.Min(available, scan.Length - (p + 8));
                if (available > 0)
                    regions.Add(new LockRegion(p + 8, available, "IDAT"));
                break;
            }
            long next = (long)p + 12 + clen;
            if (next > scan.Length) break;   // 该 chunk 跨越扫描窗口，无法继续安全遍历
            p = (int)next;
        }
        return regions;
    }
}
