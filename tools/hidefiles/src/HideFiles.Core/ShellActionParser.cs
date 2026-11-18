using System;
using System.Collections.Generic;

namespace HideFiles.Core;

/// <summary>右键菜单/启动参数解析出的一个动作</summary>
public enum ShellActionKind
{
    /// <summary>未指定动词：只把路径加入列表，不自动处理</summary>
    Open = 0,
    /// <summary>混淆（结构模式）</summary>
    Obfuscate,
    /// <summary>还原</summary>
    Restore,
    /// <summary>加密（整体模式）</summary>
    Encrypt,
    /// <summary>解密</summary>
    Decrypt,
    /// <summary>混淆工作台：按容器类型自动分派"混淆或还原"</summary>
    ObfuscateSmart,
    /// <summary>加密工作台：按容器类型自动分派"加密或解密"</summary>
    SecureSmart
}

/// <summary>启动参数解析结果</summary>
public sealed class ShellActionRequest
{
    public ShellActionKind Kind { get; init; }
    public List<string> Paths { get; init; } = new();

    /// <summary>是否会自动执行处理（Open 只加载列表）</summary>
    public bool IsAutoProcess => Kind != ShellActionKind.Open;

    /// <summary>自动执行时的保护模式；Open 无意义，返回 null</summary>
    public ProtectionMode? Mode => Kind switch
    {
        ShellActionKind.Obfuscate or ShellActionKind.Restore or ShellActionKind.ObfuscateSmart
            => ProtectionMode.StructureLock,
        ShellActionKind.Encrypt or ShellActionKind.Decrypt or ShellActionKind.SecureSmart
            => ProtectionMode.FullEncrypt,
        _ => null
    };

    /// <summary>
    /// 自动执行时是"加密方向"（true）还是"还原方向"（false）；
    /// Smart 动词返回 null，表示需按每个文件的实际容器类型逐个判定。
    /// </summary>
    public bool? IsObfuscate => Kind switch
    {
        ShellActionKind.Obfuscate or ShellActionKind.Encrypt => true,
        ShellActionKind.Restore or ShellActionKind.Decrypt => false,
        _ => null
    };
}

/// <summary>
/// 解析资源管理器右键动词传入的命令行参数。
/// 抽成纯函数是为了能脱离 GUI 做回归测试：
/// 2026-11-18 的回归（--decrypt 被硬编码为结构加密模式，导致加密文件必然解密失败）
/// 正是发生在这一段映射里。
/// </summary>
public static class ShellActionParser
{
    public static ShellActionRequest Parse(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        var kind = ShellActionKind.Open;

        foreach (string arg in args)
        {
            switch (arg)
            {
                // 新菜单：一个入口覆盖"做"与"还原"
                case "--obfuscate" or "-obfuscate":
                    kind = ShellActionKind.ObfuscateSmart;
                    break;
                case "--secure" or "-secure":
                    kind = ShellActionKind.SecureSmart;
                    break;

                // 兼容旧版右键注册表遗留的动词
                case "--hide" or "-hide":
                    kind = ShellActionKind.Obfuscate;
                    break;
                case "--unhide" or "-unhide":
                    kind = ShellActionKind.Restore;
                    break;
                case "--encrypt" or "-encrypt":
                    kind = ShellActionKind.Encrypt;
                    break;
                case "--decrypt" or "-decrypt":
                    kind = ShellActionKind.Decrypt;
                    break;

                case "--open" or "-open":
                    kind = ShellActionKind.Open;
                    break;
                default:
                    if (!arg.StartsWith('-') && !string.IsNullOrWhiteSpace(arg))
                        paths.Add(arg);
                    break;
            }
        }

        return new ShellActionRequest { Kind = kind, Paths = paths };
    }
}
