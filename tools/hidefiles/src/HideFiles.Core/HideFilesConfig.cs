using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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

    /// <summary>恢复码哈希（SHA-256 小写 Hex，64位），仅用于校验用户输入格式是否正确；恢复码明文不落盘</summary>
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
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRecoveryCode(recoveryCode)))).ToLowerInvariant();

    private static string HashRecoveryCodeLegacyBase64(string recoveryCode) =>
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
        string actualHex = HashRecoveryCode(code);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(RecoveryCodeHash), Encoding.UTF8.GetBytes(actualHex)))
        {
            return true;
        }

        // 向后兼容旧配置中的 Base64 格式
        string actualLegacy = HashRecoveryCodeLegacyBase64(code);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(RecoveryCodeHash), Encoding.UTF8.GetBytes(actualLegacy)))
        {
            RecoveryCodeHash = actualHex; // 验证通过后自动就地平滑升级为 Hex
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验 PIN 是否与配置中的哈希一致。
    /// 注意：未设置 PIN 时恒为 true，调用方应先判断 <see cref="HasPin"/>。
    /// </summary>
    public bool VerifyPin(string? pin)
    {
        if (!HasPin) return true;
        if (string.IsNullOrWhiteSpace(pin)) return false;
        string actual = HashPin(pin.Trim());
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(PinHash!), Encoding.UTF8.GetBytes(actual));
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
                new ExtensionRuleGroup("Archives", ".bak", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }),
                new ExtensionRuleGroup("Documents", ".dat", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md" }),
                new ExtensionRuleGroup("Others", ".dat", new[] { ".exe", ".dll", ".iso", ".bin", ".apk", ".dmg", ".db" })
            }
        };
        return cfg;
    }

    /// <summary>数据目录覆盖（自动化测试 / CI 隔离用，优先级最高）。</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>环境变量名：设置后覆盖数据目录（便携部署 / 自动化测试用）。</summary>
    public const string DataDirEnvVarName = "HIDEFILES_DATA_DIR";

    public static string PortableFlagPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.ini");

    public static string PortableDataDirPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_data");

    public static string AppDataDirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HideFiles");

    public static bool IsPortableMode =>
        File.Exists(PortableFlagPath) || Directory.Exists(PortableDataDirPath);

    public static string DeploymentModeName =>
        IsPortableMode ? "便携模式 (Portable)" : "标准模式 (Roaming)";

    public static string DirPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DataDirOverride))
                return DataDirOverride;

            try
            {
                var env = Environment.GetEnvironmentVariable(DataDirEnvVarName);
                if (!string.IsNullOrWhiteSpace(env)) return env;
            }
            catch { }

            if (IsPortableMode)
                return PortableDataDirPath;

            string localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(localConfig))
                return AppDomain.CurrentDomain.BaseDirectory;

            return AppDataDirPath;
        }
    }

    public static string GetDefaultConfigPath()
    {
        string dir = DirPath;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static HideFilesConfig Load(string? path = null)
    {
        LastLoadWasCorrupted = false;
        LastLoadBackupPath = null;

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
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter() }
            };
            var loaded = JsonSerializer.Deserialize<HideFilesConfig>(json, opt);
            if (loaded != null)
            {
                EnsureDefaultGroups(loaded);
                return loaded;
            }
        }
        catch
        {
            // 落到下面的损坏处理
        }

        // 配置已损坏：先另存备份再返回默认值，避免下一次 Save 把残存的密钥材料彻底覆盖掉。
        // 用户可凭备份文件手工恢复；备份失败也不阻塞启动。
        LastLoadBackupPath = TryBackupCorrupt(filePath);
        LastLoadWasCorrupted = true;
        return CreateDefault();
    }

    /// <summary>
    /// 最近一次 <see cref="Load"/> 是否因配置损坏而回退到默认值。
    /// 为 true 时密钥材料已不在内存中，调用方必须提示用户（而不是静默让其重设密码，
    /// 那会生成新主密钥，导致历史文件用新密码永久打不开）。
    /// </summary>
    public static bool LastLoadWasCorrupted { get; private set; }

    /// <summary>损坏配置的备份路径（Load 回退时写入，供调用方提示用户）</summary>
    public static string? LastLoadBackupPath { get; private set; }

    /// <summary>把损坏的配置文件另存为 config.json.corrupt-yyyyMMddHHmmss，返回备份路径（失败返回 null）</summary>
    private static string? TryBackupCorrupt(string filePath)
    {
        try
        {
            string backup = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            if (File.Exists(backup)) backup = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmssfff}";
            File.Copy(filePath, backup, overwrite: false);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureDefaultGroups(HideFilesConfig config)
    {
        if (!config.Groups.Any(g => g.Name.Equals("Documents", StringComparison.OrdinalIgnoreCase)))
        {
            config.Groups.Add(new ExtensionRuleGroup("Documents", ".dat",
                new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md" }));
        }
        if (!config.Groups.Any(g => g.Name.Equals("Others", StringComparison.OrdinalIgnoreCase) ||
                                    g.Name.Equals("其它", StringComparison.OrdinalIgnoreCase)))
        {
            config.Groups.Add(new ExtensionRuleGroup("Others", ".dat",
                new[] { ".exe", ".dll", ".iso", ".bin", ".apk", ".dmg", ".db" }));
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
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
        string json = JsonSerializer.Serialize(this, opt);

        // 原子写：先写同目录临时文件再 rename 覆盖。
        // 直接 File.WriteAllText 在断电/磁盘满时会留下半截 JSON，
        // 下次 Load 解析失败即静默丢弃全部密钥材料（含主密钥封装），历史文件只能靠恢复码。
        string tmp = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public bool MatchExtension(string fileExt, out string fakeExt)
    {
        string ext = fileExt.StartsWith('.') ? fileExt.ToLowerInvariant() : "." + fileExt.ToLowerInvariant();
        // 1. 匹配启用的规则组
        foreach (var g in Groups)
        {
            if (g.Enabled && g.Extensions.Contains(ext))
            {
                fakeExt = g.FakeExtension;
                return true;
            }
        }

        // 2. 未匹配特定规则组时，若“其它/Others”组启用，则取其伪装扩展名
        var otherGroup = Groups.FirstOrDefault(g => g.Enabled && (
            g.Name.Equals("Others", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Equals("其它", StringComparison.OrdinalIgnoreCase)));
        fakeExt = otherGroup?.FakeExtension ?? DefaultFakeExtension;
        return false;
    }
}
