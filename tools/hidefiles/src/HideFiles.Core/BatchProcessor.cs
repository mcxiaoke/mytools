using System;
using System.Collections.Generic;
using System.IO;

namespace HideFiles.Core;

public sealed class BatchProgress
{
    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double ProgressPercentage => TotalCount == 0 ? 0 : (double)ProcessedCount / TotalCount * 100.0;
}

public sealed class BatchSummary
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public List<ProcessFileResult> Results { get; set; } = new();
}

public sealed class BatchProcessor
{
    private readonly FileObfuscator _obfuscator = new();

    /// <summary>
    /// 展开路径为待处理文件清单。
    /// filterByExtensionRules：仅结构锁需要按扩展名规则筛选（整体加密对任意文件都有效）；
    /// 已保护的文件（含容器魔数）不会被再次收集。
    /// </summary>
    public List<string> CollectFiles(IEnumerable<string> paths, HideFilesConfig config, bool forObfuscate,
        bool filterByExtensionRules = true, bool? recursive = null)
    {
        var targetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                targetFiles.Add(Path.GetFullPath(p));
            }
            else if (Directory.Exists(p))
            {
                bool recurse = recursive ?? config.IncludeSubdirectories;
                var opt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var allFiles = Directory.EnumerateFiles(p, "*.*", opt);

                foreach (var f in allFiles)
                {
                    string full = Path.GetFullPath(f);
                    if (forObfuscate)
                    {
                        // 混淆时：按需匹配规则，并跳过已经保护过的文件
                        if (filterByExtensionRules && !config.MatchExtension(Path.GetExtension(f), out _))
                            continue;
                        if (IsProtected(full)) continue;
                        targetFiles.Add(full);
                    }
                    else
                    {
                        // 还原时：收集已被混淆的文件
                        targetFiles.Add(full);
                    }
                }
            }
        }

        return new List<string>(targetFiles);
    }

    private static bool IsProtected(string path) => V2Container.IsV2File(path) || V2Tail.IsTailFile(path);

    public BatchSummary ObfuscateBatch(IEnumerable<string> filePaths, HideFilesConfig config,
        IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new List<string>(filePaths);
        var summary = new BatchSummary { Total = files.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var prog = new BatchProgress { TotalCount = files.Count };

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string f = files[i];
            prog.CurrentFile = Path.GetFileName(f);

            var res = _obfuscator.ObfuscateFile(f, config, cancellationToken);
            summary.Results.Add(res);

            prog.ProcessedCount++;
            if (res.Success)
            {
                summary.Succeeded++;
                prog.SuccessCount++;
            }
            else
            {
                summary.Failed++;
                prog.FailedCount++;
            }

            progress?.Report(prog);
        }

        sw.Stop();
        summary.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        return summary;
    }

    public BatchSummary RestoreBatch(IEnumerable<string> filePaths, string? pin, string? recoveryCode = null,
        IProgress<BatchProgress>? progress = null, ProtectionMode? expectedMode = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<string>(filePaths);
        var summary = new BatchSummary { Total = files.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var prog = new BatchProgress { TotalCount = files.Count };

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string f = files[i];
            prog.CurrentFile = Path.GetFileName(f);

            var res = _obfuscator.RestoreFile(f, pin, recoveryCode, expectedMode, cancellationToken);
            summary.Results.Add(res);

            prog.ProcessedCount++;
            if (res.Success)
            {
                summary.Succeeded++;
                prog.SuccessCount++;
            }
            else
            {
                summary.Failed++;
                prog.FailedCount++;
            }

            progress?.Report(prog);
        }

        sw.Stop();
        summary.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        return summary;
    }
}
