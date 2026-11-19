using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HideFiles.Core;

namespace HideFiles.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch { }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[取消] 收到中断信号，正在安全中止...");
        };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        var targetArgs = new List<string>();
        bool dryRun = false;
        string? pin = null;
        string? recovery = null;
        string? currentPin = null;
        ProtectionMode? modeOverride = null;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (a.Equals("--pin", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pin = args[++i];
            }
            else if (a.Equals("--recovery", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                recovery = args[++i];
            }
            else if (a.Equals("--current", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                currentPin = args[++i];
            }
            else if (a.Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string m = args[++i];
                modeOverride = m.Equals("full", StringComparison.OrdinalIgnoreCase)
                    ? ProtectionMode.FullEncrypt : ProtectionMode.StructureLock;
            }
            else
            {
                targetArgs.Add(a);
            }
        }

        var config = HideFilesConfig.Load();
        if (modeOverride.HasValue)
        {
            config.ProtectionMode = modeOverride.Value;
        }
        if (!string.IsNullOrEmpty(pin))
        {
            config.SessionPin = pin;
        }

        switch (command)
        {
            case "hide":
                if (!modeOverride.HasValue) config.ProtectionMode = ProtectionMode.StructureLock; // 混淆 = 结构锁
                return ExecuteHide(targetArgs, config, dryRun, cts.Token);

            case "encrypt":
                if (!modeOverride.HasValue) config.ProtectionMode = ProtectionMode.FullEncrypt;   // 加密 = 整体加密模式
                return ExecuteHide(targetArgs, config, dryRun, cts.Token);

            case "unhide":
            case "restore":
            case "decrypt":
                // 还原/解密：容器自带模式标记，自动识别（结构锁尾容器 或 整体加密前置容器）
                return ExecuteUnhide(targetArgs, dryRun, pin, recovery, cts.Token);

            case "setpin":
                return ExecuteSetPin(targetArgs, config, currentPin);

            case "scan":
            case "status":
                return ExecuteScan(targetArgs, config, pin);

            case "dir-hide":
                return ExecuteDirectoryNames(targetArgs, obfuscate: true);

            case "dir-unhide":
                return ExecuteDirectoryNames(targetArgs, obfuscate: false);

            default:
                if (File.Exists(args[0]) || Directory.Exists(args[0]))
                {
                    targetArgs.Insert(0, args[0]);
                    return ExecuteSmartAction(targetArgs, config, pin, recovery, cts.Token);
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[错误] 未知命令 \"{command}\"，用 --help 查看用法。");
                Console.ResetColor();
                return 1;
        }
    }

    private static int ExecuteSetPin(List<string> args, HideFilesConfig config, string? currentPin)
    {
        if (args.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[错误] 用法：CarroCasketCli setpin <新密码> [--current <当前密码>]");
            Console.ResetColor();
            return 1;
        }

        var result = config.ChangePin(args[0], currentPin);
        if (!result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[错误] {result.ErrorMessage}");
            Console.ResetColor();
            if (config.HasKeyMaterial && string.IsNullOrEmpty(currentPin))
                Console.WriteLine("        修改密码需加 --current <当前密码>；主密钥与恢复码不变。");
            return 1;
        }

        config.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        if (result.MasterKeyRecreated)
        {
            Console.WriteLine("[完成] 密码已设置。请妥善保存恢复码（仅显示一次）：");
            Console.ResetColor();
            Console.WriteLine($"  恢复码：{result.RecoveryCode}");
        }
        else
        {
            Console.WriteLine("[完成] 密码已更新：主密钥与恢复码不变，此前加密的文件仍可用新密码解密。");
            Console.ResetColor();
        }
        return 0;
    }

    private static int ExecuteHide(List<string> paths, HideFilesConfig config, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            Console.WriteLine("[提示] 请指定要处理的文件或目录。");
            return 1;
        }

        var processor = new BatchProcessor();
        // 混淆只处理规则内的媒体格式；加密对任意文件都有效，不做扩展名过滤
        bool filterByRules = config.ProtectionMode == ProtectionMode.StructureLock;
        var files = processor.CollectFiles(paths, config, forObfuscate: true, filterByExtensionRules: filterByRules);

        if (files.Count == 0)
        {
            Console.WriteLine(filterByRules
                ? "[提示] 没有可混淆的文件（混淆仅支持 mp4 / mkv / jpg / png 等媒体格式）。其它格式请改用 encrypt。"
                : "[提示] 没有找到可处理的文件。");
            return 0;
        }

        Console.WriteLine($"找到 {files.Count} 个文件。");
        if (dryRun)
        {
            Console.WriteLine("[预演] 将会处理：");
            foreach (var f in files)
            {
                config.MatchExtension(Path.GetExtension(f), out string fakeExt);
                Console.WriteLine($"  * {f}  →  {fakeExt}");
            }
            return 0;
        }

        var obfuscator = new FileObfuscator();
        int success = 0;
        int failed = 0;
        var sw = Stopwatch.StartNew();

        foreach (var f in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("\n[取消] 批量处理已中止。");
                break;
            }

            var res = obfuscator.ObfuscateFile(f, config, cancellationToken);
            if (res.Success)
            {
                success++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)} → {Path.GetFileName(res.FinalPath)}（{res.ElapsedMilliseconds:F1}ms）");
            }
            else
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)}: {res.ErrorMessage}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n完成：成功 {success}，失败 {failed}，用时 {sw.ElapsedMilliseconds:F1} ms。");

        return failed == 0 ? 0 : 2;
    }

    private static int ExecuteUnhide(List<string> paths, bool dryRun, string? pin, string? recovery, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            Console.WriteLine("[提示] 请指定要解密的文件或目录。");
            return 1;
        }
        if (string.IsNullOrEmpty(pin) && string.IsNullOrEmpty(recovery))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[错误] 解密需要 --pin <密码> 或 --recovery <恢复码>。");
            Console.ResetColor();
            return 1;
        }

        var config = HideFilesConfig.Load();
        var processor = new BatchProcessor();
        var allCandidates = processor.CollectFiles(paths, config, forObfuscate: false);

        var obfuscator = new FileObfuscator();
        byte[]? masterKey = TryGetLocalMasterKey(pin);
        var obfuscatedFiles = new List<string>();

        foreach (var f in allCandidates)
        {
            var insp = obfuscator.Inspect(f, masterKey);
            if (insp.Status == FileStatus.Obfuscated)
            {
                obfuscatedFiles.Add(f);
            }
        }

        if (obfuscatedFiles.Count == 0)
        {
            Console.WriteLine("[提示] 指定路径下没有已加密的文件。");
            return 0;
        }

        Console.WriteLine($"找到 {obfuscatedFiles.Count} 个已加密文件。");
        if (dryRun)
        {
            Console.WriteLine("[预演] 将会解密：");
            foreach (var f in obfuscatedFiles)
            {
                var insp = obfuscator.Inspect(f);
                Console.WriteLine($"  * {f}  →  {insp.OriginalFileName ?? "-"}");
            }
            return 0;
        }

        int success = 0;
        int failed = 0;
        var sw = Stopwatch.StartNew();

        foreach (var f in obfuscatedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("\n[取消] 批量解密已中止。");
                break;
            }

            var res = obfuscator.RestoreFile(f, pin, recovery, cancellationToken: cancellationToken);
            if (res.Success)
            {
                success++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)} → {Path.GetFileName(res.FinalPath)}（{res.ElapsedMilliseconds:F1}ms）");
            }
            else
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)}: {res.ErrorMessage}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n完成：成功 {success}，失败 {failed}，用时 {sw.ElapsedMilliseconds:F1} ms。");

        return failed == 0 ? 0 : 2;
    }

    private static int ExecuteScan(List<string> paths, HideFilesConfig config, string? pin)
    {
        if (paths.Count == 0)
        {
            paths.Add(Directory.GetCurrentDirectory());
        }

        var processor = new BatchProcessor();
        // 查看状态时总是递归：scan 是只读盘点，不应受"是否包含子目录"的批量设置影响
        var files = processor.CollectFiles(paths, config, forObfuscate: false, recursive: true);
        var obfuscator = new FileObfuscator();
        byte[]? masterKey = TryGetLocalMasterKey(pin);   // 有 PIN 时顺带显示原始文件名

        int normal = 0;
        int obfuscated = 0;
        int corrupted = 0;

        Console.WriteLine($"扫描 {files.Count} 个文件...\n");
        Console.WriteLine($"{"状态",-12} | {"大小",-10} | {"当前文件名",-38} | 原始文件名");
        Console.WriteLine(new string('-', 88));

        foreach (var f in files)
        {
            var insp = obfuscator.Inspect(f, masterKey);
            string statusStr;
            ConsoleColor col;

            switch (insp.Status)
            {
                case FileStatus.Obfuscated:
                    obfuscated++;
                    statusStr = "已混淆/加密";
                    col = ConsoleColor.Yellow;
                    break;
                case FileStatus.Normal:
                    normal++;
                    statusStr = "未处理";
                    col = ConsoleColor.Gray;
                    break;
                default:
                    corrupted++;
                    statusStr = insp.Status switch
                    {
                        FileStatus.Corrupted => "损坏",
                        FileStatus.Locked => "被占用",
                        FileStatus.NotFound => "未找到",
                        _ => "未知"
                    };
                    col = ConsoleColor.Red;
                    break;
            }

            Console.ForegroundColor = col;
            Console.Write($"{statusStr,-12} ");
            Console.ResetColor();
            Console.WriteLine($"| {FormatSize(insp.FileSize),-10} | {Truncate(Path.GetFileName(f), 35),-35} | {Truncate(insp.OriginalFileName ?? "-", 25),-25}");
        }

        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"汇总：共 {files.Count}，未处理 {normal}，已混淆/加密 {obfuscated}，异常 {corrupted}。");
        return 0;
    }

    private static int ExecuteSmartAction(List<string> paths, HideFilesConfig config, string? pin, string? recovery, CancellationToken cancellationToken = default)
    {
        var obfuscator = new FileObfuscator();
        bool anyObfuscated = false;

        foreach (var p in paths)
        {
            if (File.Exists(p) && obfuscator.Inspect(p).Status == FileStatus.Obfuscated)
            {
                anyObfuscated = true;
                break;
            }
        }

        // 如果包含已加密文件，智能执行 unhide；否则执行 hide
        return anyObfuscated ? ExecuteUnhide(paths, false, pin, recovery, cancellationToken) : ExecuteHide(paths, config, false, cancellationToken);
    }

    private static int ExecuteDirectoryNames(List<string> paths, bool obfuscate)
    {
        if (paths.Count == 0)
        {
            Console.WriteLine($"[错误] 请指定目录：CarroCasketCli {(obfuscate ? "dir-hide" : "dir-unhide")} <目录...>");
            return 1;
        }

        int failed = 0;
        foreach (string path in paths)
        {
            var result = obfuscate
                ? DirectoryObfuscator.ObfuscateDirectory(path)
                : DirectoryObfuscator.RestoreDirectory(path);

            Console.ForegroundColor = result.Success ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write($"  [{(result.Success ? "OK" : "WARN")}] ");
            Console.ResetColor();
            string verb = obfuscate ? "伪装" : "还原";
            Console.WriteLine($"{path}：{verb} {result.Renamed}，跳过 {result.Skipped}，失败 {result.Failures.Count}");

            foreach (string failure in result.Failures)
                Console.WriteLine($"         {failure}");

            if (!result.Success) failed++;
        }

        return failed == 0 ? 0 : 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"{AppBrand.Name} - {AppBrand.Tagline}");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("用法：");
        Console.WriteLine("  CarroCasketCli setpin <密码> [--current <当前密码>]");
        Console.WriteLine("      设置密码并输出恢复码；修改密码需提供当前密码。");
        Console.WriteLine("  CarroCasketCli hide <路径...> --pin <密码> [--dry-run]");
        Console.WriteLine("      混淆：原地混淆文件头部与关键结构（仅媒体格式）。");
        Console.WriteLine("  CarroCasketCli encrypt <路径...> --pin <密码> [--dry-run]");
        Console.WriteLine("      加密：全文件分块 AES-256-GCM 加密（任意格式）。");
        Console.WriteLine("  CarroCasketCli unhide <路径...> (--pin <密码> | --recovery <恢复码>) [--dry-run]");
        Console.WriteLine("      还原/解密：自动识别容器格式（unhide / restore / decrypt 等价）。");
        Console.WriteLine("  CarroCasketCli scan [路径...]");
        Console.WriteLine("      查看文件状态（可混淆 / 已混淆 / 不支持 / 已加密）。");
        Console.WriteLine("  CarroCasketCli dir-hide <目录...>");
        Console.WriteLine("      伪装子目录名（添加 _hf_ 前缀）。");
        Console.WriteLine("  CarroCasketCli dir-unhide <目录...>");
        Console.WriteLine("      还原子目录名。");
        Console.WriteLine("  CarroCasketCli --help");
        Console.WriteLine("      显示本帮助。");
        Console.WriteLine();
    }

    /// <summary>
    /// 用本机 config 的密钥材料解出与 PIN 对应的主密钥；失败返回 null。
    /// 用于"用户改过 PIN，文件内 emkBlob 仍是旧 PIN 封装"的兜底解密。
    /// </summary>
    private static byte[]? TryGetLocalMasterKey(string? pin)
    {
        if (string.IsNullOrEmpty(pin)) return null;
        try
        {
            var cfg = HideFilesConfig.Load();
            return cfg.HasKeyMaterial ? cfg.UnwrapMasterKeyWithPin(pin) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)    {
        if (bytes >= 1024 * 1024) return $"{(double)bytes / 1024 / 1024:F2} MB";
        if (bytes >= 1024) return $"{(double)bytes / 1024:F1} KB";
        return $"{bytes} B";
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
    }
}
