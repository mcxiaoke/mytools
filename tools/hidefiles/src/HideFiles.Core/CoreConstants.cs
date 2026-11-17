namespace HideFiles.Core;

/// <summary>
/// v2 容器常量。v2 为全新格式，不兼容早期"头部 XOR + 明文尾部元数据"的 v1。
/// </summary>
public static class CoreConstants
{
    /// <summary>
    /// v2 统一魔数：8 字节、避开所有已知文件签名，不泄露源格式。
    /// </summary>
    public static readonly byte[] V2Magic = new byte[] { 0x89, 0x48, 0x46, 0x0D, 0x0A, 0x1A, 0x0A, 0x9F };

    /// <summary>
    /// 结构锁模式的尾部魔数（置于文件末尾，区别于整体加密的前置 V2Magic）。
    /// </summary>
    public static readonly byte[] V2TailMagic = new byte[] { 0x8A, 0x48, 0x46, 0x0D, 0x0A, 0x1A, 0x0A, 0x9F };

    /// <summary>结构锁原地加密时，头部签名区长度（覆盖 ftyp/EBML/JPEG SOI/PNG signature）</summary>
    public const int HeadSignatureSize = 64;

    public const byte V2Version = 2;

    /// <summary>
    /// v2 固定头长度：magic(8)+ver(1)+mode(1)+flags(1)+reserved(1)
    /// + configSalt(16)+fileSalt(16)+fileNonce(12)+emkBlob(60)+metaLen(4) = 120
    /// </summary>
    public const int V2HeaderSize = 8 + 1 + 1 + 1 + 1 + 16 + 16 + 12 + (12 + 32 + 16) + 4;

    /// <summary>EMK blob = nonce(12) + AES-GCM(MK,32) 密文(32) + tag(16)</summary>
    public const int EmkBlobSize = 12 + 32 + 16;
    public const int MasterKeySize = 32;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int GcmTagSize = 16;

    /// <summary>AES-GCM 分块大小（整体加密模式）</summary>
    public const int FullChunkSize = 64 * 1024;

    /// <summary>默认 PBKDF2 迭代次数（PIN 低熵，抗暴力）；实测 60 万次约 104ms</summary>
    public const int DefaultKdfIterations = 600_000;

    /// <summary>加密后的最小文件长度：头部 + 至少一字节元数据密文</summary>
    public const int V2MinSize = V2HeaderSize + GcmTagSize;

    /// <summary>可逆混淆目录名前缀</summary>
    public const string ObfuscatedDirectoryPrefix = "_hf_";

    /// <summary>保留常量（部分旧配置字段仍引用，避免配置反序列化问题）</summary>
    public const int DefaultHeadSize = 64 * 1024;
}

/// <summary>保护模式</summary>
public enum ProtectionMode : byte
{
    /// <summary>格式结构锁：只加密关键结构，媒体主体明文，极速，防大部分人</summary>
    StructureLock = 0,
    /// <summary>整体加密：全文件分块 AES-GCM，最强保护</summary>
    FullEncrypt = 1
}
