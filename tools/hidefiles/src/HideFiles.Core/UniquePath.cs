namespace HideFiles.Core;

/// <summary>
/// 目标路径冲突保护。
///
/// 混淆与还原都会产出新文件名：还原时原名来自元数据，混淆时来自哈希/原名。
/// 这两处都不能覆盖磁盘上已存在的文件——用户可能在原地重新创建了同名文件，
/// 或两个同内容文件撞同一个哈希名。因此统一在此收口：
///   - 目标已存在时，改为"原名 (1).扩展名"递增；
///   - 移动时使用 overwrite: false，并针对"目标刚被占用"的竞态重试。
/// </summary>
internal static class UniquePath
{
    private const int MaxNameAttempts = 1000;
    private const int MaxMoveAttempts = 8;

    /// <summary>返回 dir 下不与磁盘冲突的完整路径（不移动任何文件）</summary>
    public static string Resolve(string dir, string fileName)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(stem))
        {
            stem = fileName;
            ext = string.Empty;
        }

        for (int i = 1; i <= MaxNameAttempts; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }

        throw new IOException($"无法为 {fileName} 生成不冲突的文件名（已尝试 {MaxNameAttempts} 次）。");
    }

    /// <summary>把 source 移到 dir/fileName（绝不覆盖），返回最终落盘路径</summary>
    public static string Move(string source, string dir, string fileName)
    {
        for (int attempt = 0; attempt < MaxMoveAttempts; attempt++)
        {
            string target = Resolve(dir, fileName);
            try
            {
                File.Move(source, target, overwrite: false);
                return target;
            }
            catch (IOException) when (attempt < MaxMoveAttempts - 1 && Exists(target))
            {
                // 竞态：取名后被其他进程抢占，换个序号重试
            }
        }

        throw new IOException($"无法将文件移动到 {fileName}：目标持续被占用。");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
