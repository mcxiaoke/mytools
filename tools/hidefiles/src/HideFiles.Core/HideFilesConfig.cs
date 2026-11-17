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
    /// PIN 码加盐哈希（仅用于还原门控）
    /// </summary>
    public string? PinHash { get; set; }

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinHash);

    public List<ExtensionRuleGroup> Groups { get; set; } = new();

    public void SetPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            PinHash = null;
        }
        else
        {
            byte[] salted = Encoding.UTF8.GetBytes(pin.Trim() + PinSalt);
            PinHash = Convert.ToHexString(SHA256.HashData(salted)).ToLowerInvariant();
        }
    }

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
