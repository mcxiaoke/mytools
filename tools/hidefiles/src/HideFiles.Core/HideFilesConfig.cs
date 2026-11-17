using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HideFiles.Core;

public enum NameMode
{
    KeepStemChangeExt = 0,
    HashStem = 1
}

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

public sealed class HideFilesConfig
{
    private const string PinSalt = "#HF_PIN_SALT_2026#";

    public int Version { get; set; } = 1;
    public int HeadObfuscateSize { get; set; } = CoreConstants.DefaultHeadSize;
    public string DefaultFakeExtension { get; set; } = ".dat";
    public NameMode NameObfuscationMode { get; set; } = NameMode.HashStem;
    public bool ObfuscateFileName { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = false;
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

    /// <summary>设置 PIN：生成随机 MK + configSalt，用 PIN 加密 MK，并返回恢复码</summary>
    public string SetPinV2(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            PinHash = null; ConfigSalt = null; WrappedMasterKey = null;
            RecoveryCodeHash = null; SessionPin = null;
            return string.Empty;
        }

        string normalized = pin.Trim();
        byte[] salt = RandomNumberGenerator.GetBytes(CoreConstants.SaltSize);
        byte[] mk = RandomNumberGenerator.GetBytes(CoreConstants.MasterKeySize);
        byte[] kek = V2Crypto.DeriveKek(normalized, salt, CoreConstants.DefaultKdfIterations);
        byte[] wrapped = V2Crypto.WrapMasterKey(kek, mk);
        string recovery = V2Crypto.MasterKeyToRecoveryCode(mk);

        ConfigSalt = Convert.ToBase64String(salt);
        WrappedMasterKey = Convert.ToBase64String(wrapped);
        PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized + PinSalt))).ToLowerInvariant();
        RecoveryCodeHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(recovery)));
        KdfIterations = CoreConstants.DefaultKdfIterations;
        SessionPin = normalized;
        return recovery;
    }

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
        string actual = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    public void SetPin(string? pin) => SetPinV2(pin);

    public bool VerifyPin(string? pin)
    {
        if (!HasPin) return true;
        if (string.IsNullOrWhiteSpace(pin)) return false;

        byte[] salted = Encoding.UTF8.GetBytes(pin.Trim() + PinSalt);
        string hash = Convert.ToHexString(SHA256.HashData(salted)).ToLowerInvariant();
        return string.Equals(PinHash, hash, StringComparison.OrdinalIgnoreCase);
    }

    public static HideFilesConfig CreateDefault()
    {
        var cfg = new HideFilesConfig
        {
            Version = 1,
            HeadObfuscateSize = CoreConstants.DefaultHeadSize,
            DefaultFakeExtension = ".dat",
            NameObfuscationMode = NameMode.HashStem,
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
