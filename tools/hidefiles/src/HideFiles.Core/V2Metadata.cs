using System.Text;

namespace HideFiles.Core;

/// <summary>一段需要被锁定/解锁的关键结构区域</summary>
public sealed class LockRegion
{
    public long Offset { get; set; }
    public int Length { get; set; }
    /// <summary>结构类型标识，便于诊断与未来扩展（如 stco/co64/CodecPrivate）</summary>
    public string Kind { get; set; } = string.Empty;

    public LockRegion() { }
    public LockRegion(long offset, int length, string kind)
    {
        Offset = offset; Length = length; Kind = kind;
    }
}

/// <summary>
/// v2 元数据，TLV 编码后整体 AES-GCM 加密存放。
/// Tag 设计：1=原文件名 2=原扩展名 3=原长度 4=锁定区域清单
///          5=KDF迭代数 6=原文件SHA256(32B) 7+=未来扩展（读取端跳过未知 tag）
/// </summary>
public sealed class V2Metadata
{
    public string OriginalFileName { get; set; } = string.Empty;
    public string OriginalExtension { get; set; } = string.Empty;
    public long OriginalLength { get; set; }
    public List<LockRegion> LockRegions { get; set; } = new();
    public int KdfIterations { get; set; } = CoreConstants.DefaultKdfIterations;
    public byte[] OriginalSha256 { get; set; } = Array.Empty<byte>();
    public ProtectionMode Mode { get; set; }
    /// <summary>结构锁模式：每个 LockRegion 解密后的原始 CRC32，用于快速完整性校验</summary>
    public List<uint>? RegionCrcs { get; set; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        void WriteStr(byte tag, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            ms.WriteByte(tag);
            WriteVarInt(ms, (ulong)b.Length);
            ms.Write(b);
        }

        WriteStr(1, OriginalFileName);
        WriteStr(2, OriginalExtension);

        ms.WriteByte(3); WriteVarInt(ms, 8);
        ms.Write(BitConverter.GetBytes(OriginalLength));

        ms.WriteByte(4);
        using (var rms = new MemoryStream())
        {
            WriteVarInt(rms, (ulong)LockRegions.Count);
            foreach (var r in LockRegions)
            {
                WriteVarInt(rms, (ulong)r.Offset);
                WriteVarInt(rms, (ulong)r.Length);
                byte[] kb = Encoding.UTF8.GetBytes(r.Kind);
                WriteVarInt(rms, (ulong)kb.Length);
                rms.Write(kb);
            }
            byte[] rb = rms.ToArray();
            WriteVarInt(ms, (ulong)rb.Length);
            ms.Write(rb);
        }

        ms.WriteByte(5); WriteVarInt(ms, 4);
        ms.Write(BitConverter.GetBytes(KdfIterations));

        ms.WriteByte(6); WriteVarInt(ms, (ulong)OriginalSha256.Length);
        ms.Write(OriginalSha256);

        ms.WriteByte(7); WriteVarInt(ms, 1);
        ms.WriteByte((byte)Mode);

        if (RegionCrcs != null)
        {
            ms.WriteByte(8);
            using var cms = new MemoryStream();
            WriteVarInt(cms, (ulong)RegionCrcs.Count);
            foreach (uint crc in RegionCrcs) cms.Write(BitConverter.GetBytes(crc));
            byte[] cb = cms.ToArray();
            WriteVarInt(ms, (ulong)cb.Length);
            ms.Write(cb);
        }

        return ms.ToArray();
    }

    public static V2Metadata Deserialize(ReadOnlySpan<byte> data)
    {
        var meta = new V2Metadata();
        int p = 0;
        while (p < data.Length)
        {
            byte tag = data[p++];
            if (!TryReadVarInt(data, ref p, out ulong len)) break;
            if (len > (ulong)(data.Length - p)) break;   // 同时挡住 len 超出 int 范围的畸形值
            ReadOnlySpan<byte> val = data.Slice(p, (int)len);
            p += (int)len;

            switch (tag)
            {
                case 1: meta.OriginalFileName = Encoding.UTF8.GetString(val); break;
                case 2: meta.OriginalExtension = Encoding.UTF8.GetString(val); break;
                case 3: if (val.Length == 8) meta.OriginalLength = BitConverter.ToInt64(val); break;
                case 4: ParseRegions(val, meta.LockRegions); break;
                case 5: if (val.Length == 4) meta.KdfIterations = BitConverter.ToInt32(val); break;
                case 6: meta.OriginalSha256 = val.ToArray(); break;
                case 7: if (val.Length >= 1) meta.Mode = (ProtectionMode)val[0]; break;
                case 8: ParseCrcs(val, meta); break;
                default: break; // 未知 tag：跳过，向后兼容
            }
        }
        return meta;
    }

    private static void ParseCrcs(ReadOnlySpan<byte> val, V2Metadata meta)
    {
        int p = 0;
        if (!TryReadVarInt(val, ref p, out ulong count)) return;
        var list = new List<uint>();
        for (ulong i = 0; i < count; i++)
        {
            if (p + 4 > val.Length) return;
            list.Add(BitConverter.ToUInt32(val.Slice(p, 4)));
            p += 4;
        }
        meta.RegionCrcs = list;
    }

    private static void ParseRegions(ReadOnlySpan<byte> val, List<LockRegion> list)
    {
        int p = 0;
        if (!TryReadVarInt(val, ref p, out ulong count)) return;
        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadVarInt(val, ref p, out ulong off) ||
                !TryReadVarInt(val, ref p, out ulong len) ||
                !TryReadVarInt(val, ref p, out ulong kindLen)) return;
            if (kindLen > (ulong)(val.Length - p)) return;
            if (off > long.MaxValue || len > int.MaxValue) return;   // 越界值视为损坏数据
            string kind = Encoding.UTF8.GetString(val.Slice(p, (int)kindLen));
            p += (int)kindLen;
            list.Add(new LockRegion((long)off, (int)len, kind));
        }
    }

    private static void WriteVarInt(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> data, ref int p, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (p < data.Length && shift < 64)
        {
            byte b = data[p++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
