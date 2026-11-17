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

    public List<string> CollectFiles(IEnumerable<string> paths, HideFilesConfig config, bool forObfuscate)
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
                var opt = config.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var allFiles = Directory.EnumerateFiles(p, "*.*", opt);

                foreach (var f in allFiles)
                {
                    string ext = Path.GetExtension(f);
                    if (forObfuscate)
                    {
                        // 混淆时：只收集符合规则且未混淆的文件
                        if (config.MatchExtension(ext, out _))
                        {
                            targetFiles.Add(Path.GetFullPath(f));
                        }
                    }
                    else
                    {
                        // 还原时：收集已被混淆的文件
                        targetFiles.Add(Path.GetFullPath(f));
                    }
                }
            }
        }

        return new List<string>(targetFiles);
    }

    public BatchSummary ObfuscateBatch(IEnumerable<string> filePaths, HideFilesConfig config, IProgress<BatchProgress>? progress = null)
    {
        var files = new List<string>(filePaths);
        var summary = new BatchSummary { Total = files.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var prog = new BatchProgress { TotalCount = files.Count };

        for (int i = 0; i < files.Count; i++)
        {
            string f = files[i];
            prog.CurrentFile = Path.GetFileName(f);

            var res = _obfuscator.ObfuscateFile(f, config);
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

    public BatchSummary RestoreBatch(IEnumerable<string> filePaths, IProgress<BatchProgress>? progress = null)
    {
        var files = new List<string>(filePaths);
        var summary = new BatchSummary { Total = files.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var prog = new BatchProgress { TotalCount = files.Count };

        for (int i = 0; i < files.Count; i++)
        {
            string f = files[i];
            prog.CurrentFile = Path.GetFileName(f);

            var res = _obfuscator.RestoreFile(f);
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
