using System.Security.Cryptography;
using System.Text;

namespace HideFiles.Core;

/// <summary>
/// v2 容器读取结果
/// </summary>
public sealed class V2ReadResult
{
    public V2Metadata Metadata { get; set; } = new();
    public byte[] MasterKey { get; set; } = Array.Empty<byte>();
    public byte[] FileKey { get; set; } = Array.Empty<byte>();
    public byte[] FileNonce { get; set; } = Array.Empty<byte>();
    public byte[] ConfigSalt { get; set; } = Array.Empty<byte>();
    public long MetadataOffset { get; set; }
    public int MetadataLength { get; set; }
    public long BodyOffset => MetadataOffset + MetadataLength;
}

/// <summary>
/// v2 容器写参数
/// </summary>
public sealed class V2WriteOptions
{
    public ProtectionMode Mode { get; set; }
    public byte[] MasterKey { get; set; } = Array.Empty<byte>();
    public byte[] ConfigSalt { get; set; } = Array.Empty<byte>();
    public int KdfIterations { get; set; } = CoreConstants.DefaultKdfIterations;
    public string Pin { get; set; } = string.Empty;
}

/// <summary>
/// v2 自包含容器：头部固定 120 字节，元数据区整体 GCM 加密，主体按模式处理。
/// 文件可独立搬运；PIN 或恢复码任一即可解密。
/// </summary>
public static class V2Container
{
    /// <summary>判断是否为 v2 容器</summary>
    public static bool IsV2File(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < CoreConstants.V2MinSize) return false;
            byte[] head = new byte[CoreConstants.V2Magic.Length];
            fs.ReadExactly(head);
            return head.AsSpan().SequenceEqual(CoreConstants.V2Magic);
        }
        catch { return false; }
    }

    /// <summary>
    /// 写入 v2 容器到 dstPath。先写同目录临时文件，再同卷 rename 覆盖目标，
    /// 中途失败不会留下半个目标文件；不额外产生一次全量拷贝。
    /// </summary>
    public static void Write(string dstPath, V2Metadata meta, V2WriteOptions opt,
        Action<Stream, byte[], byte[]> writeBody)
    {
        if (opt.MasterKey.Length != CoreConstants.MasterKeySize) throw new ArgumentException("Invalid master key.");
        if (string.IsNullOrEmpty(opt.Pin)) throw new ArgumentException("PIN is required to wrap master key.");

        byte[] fileSalt = RandomNumberGenerator.GetBytes(CoreConstants.SaltSize);
        byte[] fileNonce = RandomNumberGenerator.GetBytes(CoreConstants.NonceSize);
        byte[] fileKey = V2Crypto.DeriveFileKey(opt.MasterKey, fileSalt);
        byte[] kek = V2Crypto.DeriveKek(opt.Pin, opt.ConfigSalt, CoreConstants.DefaultKdfIterations);
        byte[] emkBlob = V2Crypto.WrapMasterKey(kek, opt.MasterKey);

        meta.Mode = opt.Mode;
        meta.KdfIterations = CoreConstants.DefaultKdfIterations;
        byte[] metaPlain = meta.Serialize();
        byte[] metaCipher = new byte[metaPlain.Length + CoreConstants.GcmTagSize];
        byte[] metaNonce = V2Crypto.BlockNonce(fileNonce, ulong.MaxValue); // 用保留序号区分元数据
        using (var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize))
            gcm.Encrypt(metaNonce, metaPlain, metaCipher.AsSpan(0, metaPlain.Length),
                metaCipher.AsSpan(metaPlain.Length));

        string tmp = dstPath + ".hfv2tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // 头部
                fs.Write(CoreConstants.V2Magic);
                fs.WriteByte(CoreConstants.V2Version);
                fs.WriteByte((byte)opt.Mode);
                fs.WriteByte(0); // flags
                fs.WriteByte(0); // reserved
                fs.Write(opt.ConfigSalt);
                fs.Write(fileSalt);
                fs.Write(fileNonce);
                fs.Write(emkBlob);
                fs.Write(BitConverter.GetBytes(metaCipher.Length));

                fs.Write(metaCipher);

                // 主体由调用方写入；传入 fileKey 与 fileNonce 供模式加密使用
                writeBody(fs, fileKey, fileNonce);
                fs.Flush(true);
            }

            // 同卷 rename：原子替换目标，避免 File.Copy 造成的第二遍全量写盘
            File.Move(tmp, dstPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            throw;
        }
    }

    /// <summary>
    /// 读取容器头与元数据并验证。pin 与 recoveryCode 二选一，
    /// fallbackMasterKey 为本机 config 中当前 PIN 对应的主密钥（改过 PIN 时兜底，见 ResolveMasterKey）。
    /// 返回后调用方按 Mode 与 LockRegions 处理主体。
    /// </summary>
    public static V2ReadResult Read(string path, string? pin, string? recoveryCode, byte[]? fallbackMasterKey = null)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < CoreConstants.V2MinSize) throw new InvalidDataException("File too small to be a v2 container.");
        byte[] head = new byte[CoreConstants.V2HeaderSize];
        fs.ReadExactly(head, 0, head.Length);

        if (!head.AsSpan(0, 8).SequenceEqual(CoreConstants.V2Magic))
            throw new InvalidDataException("Not a HideFiles v2 container.");
        byte version = head[8];
        if (version != CoreConstants.V2Version)
            throw new InvalidDataException($"Unsupported v2 container version: {version}");
        var mode = (ProtectionMode)head[9];

        byte[] configSalt = head.AsSpan(12, CoreConstants.SaltSize).ToArray();
        byte[] fileSalt = head.AsSpan(28, CoreConstants.SaltSize).ToArray();
        byte[] fileNonce = head.AsSpan(44, CoreConstants.NonceSize).ToArray();
        byte[] emkBlob = head.AsSpan(56, CoreConstants.EmkBlobSize).ToArray();
        int metaLen = BitConverter.ToInt32(head, 116);
        if (metaLen < CoreConstants.GcmTagSize || metaLen > fs.Length - CoreConstants.V2HeaderSize)
            throw new InvalidDataException("Corrupted metadata length.");

        byte[] masterKey = V2Crypto.ResolveMasterKey(pin, recoveryCode, configSalt, emkBlob, fallbackMasterKey);

        byte[] fileKey = V2Crypto.DeriveFileKey(masterKey, fileSalt);
        byte[] metaCipher = new byte[metaLen];
        fs.Seek(CoreConstants.V2HeaderSize, SeekOrigin.Begin);
        fs.ReadExactly(metaCipher, 0, metaLen);

        byte[] metaNonce = V2Crypto.BlockNonce(fileNonce, ulong.MaxValue);
        byte[] metaPlain = new byte[metaLen - CoreConstants.GcmTagSize];
        try
        {
            using var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize);
            gcm.Decrypt(metaNonce, metaCipher.AsSpan(0, metaPlain.Length),
                metaCipher.AsSpan(metaPlain.Length), metaPlain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidDataException("PIN 或恢复码与该文件不匹配（元数据认证失败）。", ex);
        }

        var meta = V2Metadata.Deserialize(metaPlain);
        if (meta.Mode != mode)
            throw new InvalidDataException("Mode mismatch between header and metadata.");

        return new V2ReadResult
        {
            Metadata = meta,
            MasterKey = masterKey,
            FileKey = fileKey,
            FileNonce = fileNonce,
            ConfigSalt = configSalt,
            MetadataOffset = CoreConstants.V2HeaderSize,
            MetadataLength = metaLen
        };
    }
}
