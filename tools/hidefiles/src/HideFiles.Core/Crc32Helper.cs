using System;

namespace HideFiles.Core;

public static class Crc32Helper
{
    private static readonly uint[] Table;

    static Crc32Helper()
    {
        const uint poly = 0xEDB88320;
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 8; j > 0; j--)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ poly;
                else
                    crc >>= 1;
            }
            Table[i] = crc;
        }
    }

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte tableIndex = (byte)((crc & 0xFF) ^ bytes[i]);
            crc = (crc >> 8) ^ Table[tableIndex];
        }
        return ~crc;
    }
}
