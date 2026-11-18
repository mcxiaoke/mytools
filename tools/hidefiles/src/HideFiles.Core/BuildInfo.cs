using System;
using System.Linq;
using System.Reflection;

namespace HideFiles.Core;

/// <summary>
/// 编译时注入的构建元数据与版本信息
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly TargetAssembly = typeof(BuildInfo).Assembly;

    /// <summary>应用主版本号（如 1.0.0）</summary>
    public static string Version =>
        TargetAssembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Git 提交短哈希（若非 Git 仓库则显示 dev）</summary>
    public static string GitHash { get; } = GetMetadata("GitHash", "dev");

    /// <summary>构建日期与时间（格式：yyyy-MM-dd HH:mm:ss）</summary>
    public static string BuildTime { get; } = GetMetadata("BuildTime", "Unknown");

    private static string GetMetadata(string key, string fallback)
    {
        try
        {
            return TargetAssembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
