using System;
using System.IO;
using System.Text;

namespace HideFiles.Core;

public sealed class MetadataBlock
{
    public string OriginalFileName { get; set; } = string.Empty;
    public string OriginalExtension { get; set; } = string.Empty;
    public long OriginalLength { get; set; }
    public int ObfuscatedHeadLength { get; set; }
    public uint HeadChecksum { get; set; }
    public byte[] OriginalHeadBytes { get; set; } = Array.Empty<byte>();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 1. 原始头数据
        bw.Write(OriginalHeadBytes);

        // 2. 原文件名 (2字节长度前缀 + UTF8)
        byte[] nameBytes = Encoding.UTF8.GetBytes(OriginalFileName);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);

        // 3. 原扩展名 (2字节长度前缀 + UTF8)
        byte[] extBytes = Encoding.UTF8.GetBytes(OriginalExtension);
        bw.Write((ushort)extBytes.Length);
        bw.Write(extBytes);

        // 4. 定长数值字段
        bw.Write(OriginalLength);
        bw.Write(ObfuscatedHeadLength);
        bw.Write(HeadChecksum);

        // 5. 记录当前 Payload 长度（不含自身4字节和最后的8字节Magic）
        int payloadLen = (int)ms.Length;
        bw.Write(payloadLen);

        // 6. 尾部魔数
        bw.Write(CoreConstants.MagicMarker);

        bw.Flush();
        return ms.ToArray();
    }

    public static MetadataBlock? ReadFromStream(Stream stream)
    {
        long streamLen = stream.Length;
        if (streamLen < CoreConstants.MinFooterOverhead)
        {
            return null;
        }

        // 1. 检查末尾 8 字节魔数
        stream.Seek(streamLen - 8, SeekOrigin.Begin);
        byte[] marker = new byte[8];
        stream.ReadExactly(marker, 0, 8);
        for (int i = 0; i < 8; i++)
        {
            if (marker[i] != CoreConstants.MagicMarker[i])
            {
                return null;
            }
        }

        // 2. 读取 Payload 长度
        stream.Seek(streamLen - 8 - 4, SeekOrigin.Begin);
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int payloadLen = br.ReadInt32();

        if (payloadLen <= 0 || payloadLen > streamLen - 12)
        {
            throw new InvalidDataException("Corrupted metadata payload length.");
        }

        // 3. 读取整个 Payload
        long payloadStart = streamLen - 12 - payloadLen;
        stream.Seek(payloadStart, SeekOrigin.Begin);

        var meta = new MetadataBlock();

        // 4. 读取定长尾部信息：从 payload 尾部往前推算
        // 数值字段：OriginalLength(8) + ObfuscatedHeadLength(4) + HeadChecksum(4) = 16 字节
        long fixedFieldsPos = streamLen - 12 - 16;
        stream.Seek(fixedFieldsPos, SeekOrigin.Begin);
        meta.OriginalLength = br.ReadInt64();
        meta.ObfuscatedHeadLength = br.ReadInt32();
        meta.HeadChecksum = br.ReadUInt32();

        // 5. 读取 HeadBytes
        stream.Seek(payloadStart, SeekOrigin.Begin);
        meta.OriginalHeadBytes = new byte[meta.ObfuscatedHeadLength];
        if (meta.ObfuscatedHeadLength > 0)
        {
            stream.ReadExactly(meta.OriginalHeadBytes, 0, meta.ObfuscatedHeadLength);
        }

        // 6. 读取文件名和扩展名
        ushort nameLen = br.ReadUInt16();
        byte[] nameBuf = br.ReadBytes(nameLen);
        meta.OriginalFileName = Encoding.UTF8.GetString(nameBuf);

        ushort extLen = br.ReadUInt16();
        byte[] extBuf = br.ReadBytes(extLen);
        meta.OriginalExtension = Encoding.UTF8.GetString(extBuf);

        return meta;
    }
}
