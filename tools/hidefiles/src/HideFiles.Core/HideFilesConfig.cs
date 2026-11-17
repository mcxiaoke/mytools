using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HideFiles.Core;

public sealed class ExtensionRuleGroup
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string FakeExtension { get; set; } = ".dat";
    public List<string> Extensions { get; set; } = new();

    public ExtensionRuleGroup() { }

    public ExtensionRuleGroup(string name, string fakeExt, IEnumerable<string> exts)
    {
        Name = name;
        FakeExtension = fakeExt.StartsWith('.') ? fakeExt : "." + fakeExt;
        foreach (var ext in exts)
        {
            Extensions.Add(ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant());
        }
    }
}

/// <summary>PIN 变更结果</summary>
public sealed class PinChangeResult
{
    public bool Success { get; set; }
    /// <summary>true 表示新生成了主密钥：此前保护的文件只能用旧恢复码还原</summary>
    public bool MasterKeyRecreated { get; set; }
    /// <summary>仅在新建主密钥时返回的一次性恢复码</summary>
    public string? RecoveryCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class HideFilesConfig
{
    private const string PinSalt = "#HF_PIN_SALT_2026#";

    public int Version { get; set; } = 1;
    public int HeadObfuscateSize { get; set; } = CoreConstants.DefaultHeadSize;
    public string DefaultFakeExtension { get; set; } = ".dat";
    public bool ObfuscateFileName { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = false;
    /// <summary>预留：目录名混淆尚未接入任何界面/命令，改动前请勿依赖</summary>
    public bool ObfuscateDirectoryNames { get; set; } = false;

    /// <summary>
    /// PIN 码加盐哈希（仅用于快速验证输入是否正确，不参与数据加解密）
    /// </summary>
    public string? PinHash { get; set; }

    /// <summary>
    /// v2 密钥材料：用 PIN 派生的 KEK 加密主密钥 MK（Base64）。
    /// 每个文件头部内嵌同一份 blobs 的等价物，保证文件自包含；
    /// config 存此份是为了加密新文件时复用同一个 MK（= 同一个恢复码）。
    /// </summary>
    public string? ConfigSalt { get; set; }
    public string? WrappedMasterKey { get; set; }
    public int KdfIterations { get; set; } = CoreConstants.DefaultKdfIterations;

    /// <summary>恢复码哈希（Base64 SHA256），仅用于校验用户输入格式是否正确；恢复码明文不落盘</summary>
    public string? RecoveryCodeHash { get; set; }

    /// <summary>运行时会话 PIN（内存，不序列化）：加密新文件时用于解 WrappedMasterKey</summary>
    [JsonIgnore]
    public string? SessionPin { get; set; }

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinHash);

    /// <summary>v2 默认保护模式（设置页可切换）</summary>
    public ProtectionMode ProtectionMode { get; set; } = ProtectionMode.StructureLock;

    public List<ExtensionRuleGroup> Groups { get; set; } = new();

    /// <summary>是否已持有密钥材料（可用于加解密）</summary>
    [JsonIgnore]
    public bool HasKeyMaterial => !string.IsNullOrEmpty(ConfigSalt) && !string.IsNullOrEmpty(WrappedMasterKey);

    /// <summary>
    /// 设置或修改 PIN。
    /// - 尚无密钥材料：生成新的主密钥，返回一次性恢复码（`MasterKeyRecreated = true`）。
    /// - 已有密钥材料：**必须**提供当前 PIN，仅用新 PIN 重新封装同一主密钥，
    ///   恢复码保持不变，历史文件继续可用。
    /// 绝不在"改 PIN"时重建主密钥——那会让此前保护的文件当场失效。
    /// </summary>
    public PinChangeResult ChangePin(string? newPin, string? currentPin = null)
    {
        if (string.IsNullOrWhiteSpace(newPin))
            return new PinChangeResult { Success = false, ErrorMessage = "PIN 不能为空。" };

        string normalized = newPin.Trim();
        bool recreated = !HasKeyMaterial;
        byte[] mk;

        if (recreated)
        {
            mk = RandomNumberGenerator.GetBytes(CoreConstants.MasterKeySize);
        }
        else
        {
            if (string.IsNullOrEmpty(currentPin))
                return new PinChangeResult { Success = false, ErrorMessage = "修改 PIN 需要先验证当前 PIN。" };
            try
            {
                mk = UnwrapMasterKeyWithPin(currentPin);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return new PinChangeResult { Success = false, ErrorMessage = "当前 PIN 不正确。" };
            }
        }

        byte[] salt = RandomNumberGenerator.GetBytes(CoreConstants.SaltSize);
        byte[] kek = V2Crypto.DeriveKek(normalized, salt, CoreConstants.DefaultKdfIterations);
        byte[] wrapped = V2Crypto.WrapMasterKey(kek, mk);

        ConfigSalt = Convert.ToBase64String(salt);
        WrappedMasterKey = Convert.ToBase64String(wrapped);
        PinHash = HashPin(normalized);
        KdfIterations = CoreConstants.DefaultKdfIterations;
        SessionPin = normalized;

        string? recovery = null;
        if (recreated)
        {
            recovery = V2Crypto.MasterKeyToRecoveryCode(mk);
            RecoveryCodeHash = HashRecoveryCode(recovery);
        }

        return new PinChangeResult
        {
            Success = true,
            MasterKeyRecreated = recreated,
            RecoveryCode = recovery
        };
    }

    /// <summary>
    /// 清除全部密钥材料（等价于放弃用 PIN 解密的能力）。
    /// 历史文件仍可用当前恢复码还原，清除前应确保已离线保存恢复码。
    /// </summary>
    public void ClearKeyMaterial()
    {
        PinHash = null;
        ConfigSalt = null;
        WrappedMasterKey = null;
        RecoveryCodeHash = null;
        SessionPin = null;
    }

    private static string HashPin(string normalizedPin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPin + PinSalt))).ToLowerInvariant();

    private static string HashRecoveryCode(string recoveryCode) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRecoveryCode(recoveryCode))));

    private static string NormalizeRecoveryCode(string code) =>
        code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

    /// <summary>用 PIN 解开 config 中的主密钥 MK；失败抛认证异常</summary>
    public byte[] UnwrapMasterKeyWithPin(string? pin)
    {
        if (string.IsNullOrEmpty(ConfigSalt) || string.IsNullOrEmpty(WrappedMasterKey))
            throw new InvalidOperationException("v2 key material is not initialized. Set a PIN first.");
        byte[] salt = Convert.FromBase64String(ConfigSalt);
        byte[] wrapped = Convert.FromBase64String(WrappedMasterKey);
        byte[] kek = V2Crypto.DeriveKek((pin ?? string.Empty).Trim(), salt, KdfIterations);
        return V2Crypto.UnwrapMasterKey(kek, wrapped);
    }

    public bool VerifyRecoveryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(RecoveryCodeHash)) return false;
        string expected = RecoveryCodeHash;
        string actual = HashRecoveryCode(code);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    /// <summary>
    /// 校验 PIN 是否与配置中的哈希一致。
    /// 注意：未设置 PIN 时恒为 true，调用方应先判断 <see cref="HasPin"/>。
    /// </summary>
    public bool VerifyPin(string? pin)
    {
        if (!HasPin) return true;
        if (string.IsNullOrWhiteSpace(pin)) return false;
        return string.Equals(PinHash, HashPin(pin.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    public static HideFilesConfig CreateDefault()
    {
        var cfg = new HideFilesConfig
        {
            Version = 1,
            HeadObfuscateSize = CoreConstants.DefaultHeadSize,
            DefaultFakeExtension = ".dat",
            ObfuscateFileName = true,
            IncludeSubdirectories = false,
            ObfuscateDirectoryNames = false,
            PinHash = null,
            ConfigSalt = null,
            WrappedMasterKey = null,
            RecoveryCodeHash = null,
            KdfIterations = CoreConstants.DefaultKdfIterations,
            ProtectionMode = ProtectionMode.StructureLock,
            Groups = new List<ExtensionRuleGroup>
            {
                new ExtensionRuleGroup("Videos", ".dat", new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".ts", ".m4v" }),
                new ExtensionRuleGroup("Images", ".tmp", new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".heic", ".raw" }),
                new ExtensionRuleGroup("Audio", ".bin", new[] { ".flac", ".mp3", ".wav", ".aac", ".ogg", ".m4a" }),
                new ExtensionRuleGroup("Archives", ".bak", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" })
            }
        };
        return cfg;
    }

    public static string GetDefaultConfigPath()
    {
        string localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        if (File.Exists(localConfig))
        {
            return localConfig;
        }

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HideFiles");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "config.json");
    }

    public static HideFilesConfig Load(string? path = null)
    {
        string filePath = path ?? GetDefaultConfigPath();
        if (!File.Exists(filePath))
        {
            var def = CreateDefault();
            def.Save(filePath);
            return def;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var opt = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var loaded = JsonSerializer.Deserialize<HideFilesConfig>(json, opt);
            return loaded ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(string? path = null)
    {
        string filePath = path ?? GetDefaultConfigPath();
        string dir = Path.GetDirectoryName(filePath)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        string json = JsonSerializer.Serialize(this, opt);
        File.WriteAllText(filePath, json);
    }

    public bool MatchExtension(string fileExt, out string fakeExt)
    {
        string ext = fileExt.StartsWith('.') ? fileExt.ToLowerInvariant() : "." + fileExt.ToLowerInvariant();
        foreach (var g in Groups)
        {
            if (g.Enabled && g.Extensions.Contains(ext))
            {
                fakeExt = g.FakeExtension;
                return true;
            }
        }
        fakeExt = DefaultFakeExtension;
        return false;
    }
}
