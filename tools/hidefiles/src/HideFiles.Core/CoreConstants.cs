using System.Text;

namespace HideFiles.Core;

public static class CoreConstants
{
    /// <summary>
    /// 文件尾部固定 8 字节私有标识魔数：HF_V1\xAA\xBB\xCC
    /// </summary>
    public static readonly byte[] MagicMarker = new byte[] { 0x48, 0x46, 0x5F, 0x56, 0x31, 0xAA, 0xBB, 0xCC };

    /// <summary>
    /// 默认头部混淆大小（64 KB）
    /// </summary>
    public const int DefaultHeadSize = 64 * 1024;

    /// <summary>
    /// 最小尾部元数据包大小（Magic 8B + 长度与原大小等结构）
    /// </summary>
    public const int MinFooterOverhead = 8 + 4 + 4 + 8 + 4; // 28 bytes

    /// <summary>
    /// 可逆混淆目录名前缀
    /// </summary>
    public const string ObfuscatedDirectoryPrefix = "_hf_";
}
