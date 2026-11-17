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

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        var targetArgs = new List<string>();
        bool dryRun = false;
        bool hashName = false;
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
            else if (a.Equals("--hash-name", StringComparison.OrdinalIgnoreCase))
            {
                hashName = true;
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
        if (hashName)
        {
            config.NameObfuscationMode = NameMode.HashStem;
        }
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
                return ExecuteHide(targetArgs, config, dryRun);

            case "encrypt":
                if (!modeOverride.HasValue) config.ProtectionMode = ProtectionMode.FullEncrypt;   // 加密 = 整体加密
                return ExecuteHide(targetArgs, config, dryRun);

            case "unhide":
            case "restore":
            case "decrypt":
                // 还原/解密：容器自带模式标记，自动识别（结构锁尾容器 或 整体加密前置容器）
                return ExecuteUnhide(targetArgs, dryRun, pin, recovery);

            case "setpin":
                return ExecuteSetPin(targetArgs, config, currentPin);

            case "scan":
            case "status":
                return ExecuteScan(targetArgs, config);

            case "menu":
                return ExecuteMenu(targetArgs);

            default:
                if (File.Exists(args[0]) || Directory.Exists(args[0]))
                {
                    targetArgs.Insert(0, args[0]);
                    return ExecuteSmartAction(targetArgs, config, pin, recovery);
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Unknown command '{command}'. Use --help to view available commands.");
                Console.ResetColor();
                return 1;
        }
    }

    private static int ExecuteSetPin(List<string> args, HideFilesConfig config, string? currentPin)
    {
        if (args.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Usage: hidefiles setpin <new-pin> [--current <old-pin>]");
            Console.ResetColor();
            return 1;
        }

        var result = config.ChangePin(args[0], currentPin);
        if (!result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] {result.ErrorMessage}");
            Console.ResetColor();
            if (config.HasKeyMaterial && string.IsNullOrEmpty(currentPin))
                Console.WriteLine("        修改已有 PIN 时请追加 --current <当前PIN>；主密钥不变，恢复码保持有效。");
            return 1;
        }

        config.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        if (result.MasterKeyRecreated)
        {
            Console.WriteLine("[Success] PIN 已设置。请妥善保存下面的恢复码（忘记 PIN 时可用它还原文件）：");
            Console.ResetColor();
            Console.WriteLine($"  Recovery Code: {result.RecoveryCode}");
        }
        else
        {
            Console.WriteLine("[Success] PIN 已更新：主密钥未变，恢复码保持有效，此前保护的文件仍可用新 PIN 还原。");
            Console.ResetColor();
        }
        Console.WriteLine($"  Protection Mode: {config.ProtectionMode}");
        return 0;
    }

    private static int ExecuteHide(List<string> paths, HideFilesConfig config, bool dryRun)
    {
        if (paths.Count == 0)
        {
            Console.WriteLine("[Warning] No file or directory path specified to hide.");
            return 1;
        }

        var processor = new BatchProcessor();
        var files = processor.CollectFiles(paths, config, forObfuscate: true);

        if (files.Count == 0)
        {
            Console.WriteLine("[Info] No matching files found to obfuscate according to rules.");
            return 0;
        }

        Console.WriteLine($"--> Found {files.Count} candidate file(s) for obfuscation.");
        if (dryRun)
        {
            Console.WriteLine("[Dry Run Mode] The following files would be obfuscated:");
            foreach (var f in files)
            {
                config.MatchExtension(Path.GetExtension(f), out string fakeExt);
                Console.WriteLine($"  * {f}  --> [*{fakeExt}]");
            }
            return 0;
        }

        var obfuscator = new FileObfuscator();
        int success = 0;
        int failed = 0;
        var sw = Stopwatch.StartNew();

        foreach (var f in files)
        {
            var res = obfuscator.ObfuscateFile(f, config);
            if (res.Success)
            {
                success++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)} -> {Path.GetFileName(res.FinalPath)} ({res.ElapsedMilliseconds:F1}ms)");
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
        Console.WriteLine($"\nCompleted in {sw.ElapsedMilliseconds:F1} ms. Succeeded: {success}, Failed: {failed}.");

        return failed == 0 ? 0 : 2;
    }

    private static int ExecuteUnhide(List<string> paths, bool dryRun, string? pin, string? recovery)
    {
        if (paths.Count == 0)
        {
            Console.WriteLine("[Warning] No file or directory path specified to restore.");
            return 1;
        }
        if (string.IsNullOrEmpty(pin) && string.IsNullOrEmpty(recovery))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] 还原需要 --pin <PIN> 或 --recovery <恢复码>。");
            Console.ResetColor();
            return 1;
        }

        var config = HideFilesConfig.Load();
        var processor = new BatchProcessor();
        var allCandidates = processor.CollectFiles(paths, config, forObfuscate: false);

        var obfuscator = new FileObfuscator();
        // 改过 PIN 时文件内的 emkBlob 仍是旧 PIN 封装，用本机 config 的主密钥兜底（只算一次 PBKDF2）
        byte[]? fallbackMasterKey = TryGetLocalMasterKey(pin);
        var obfuscatedFiles = new List<string>();

        foreach (var f in allCandidates)
        {
            var insp = obfuscator.Inspect(f);
            if (insp.Status == FileStatus.Obfuscated)
            {
                obfuscatedFiles.Add(f);
            }
        }

        if (obfuscatedFiles.Count == 0)
        {
            Console.WriteLine("[Info] No obfuscated files found in the specified paths.");
            return 0;
        }

        Console.WriteLine($"--> Found {obfuscatedFiles.Count} obfuscated file(s) to restore.");
        if (dryRun)
        {
            Console.WriteLine("[Dry Run Mode] The following files would be restored:");
            foreach (var f in obfuscatedFiles)
            {
                var insp = obfuscator.Inspect(f);
                Console.WriteLine($"  * {f}  --> [{insp.OriginalFileName}]");
            }
            return 0;
        }

        int success = 0;
        int failed = 0;
        var sw = Stopwatch.StartNew();

        foreach (var f in obfuscatedFiles)
        {
            var res = obfuscator.RestoreFile(f, pin, recovery, fallbackMasterKey);
            if (res.Success)
            {
                success++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK] ");
                Console.ResetColor();
                Console.WriteLine($"{Path.GetFileName(f)} -> {Path.GetFileName(res.FinalPath)} ({res.ElapsedMilliseconds:F1}ms)");
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
        Console.WriteLine($"\nCompleted in {sw.ElapsedMilliseconds:F1} ms. Succeeded: {success}, Failed: {failed}.");

        return failed == 0 ? 0 : 2;
    }

    private static int ExecuteScan(List<string> paths, HideFilesConfig config)
    {
        if (paths.Count == 0)
        {
            paths.Add(Directory.GetCurrentDirectory());
        }

        var processor = new BatchProcessor();
        var files = processor.CollectFiles(paths, config, forObfuscate: false);
        var obfuscator = new FileObfuscator();

        int normal = 0;
        int obfuscated = 0;
        int corrupted = 0;

        Console.WriteLine($"Scanning {files.Count} files...\n");
        Console.WriteLine($"{"Status",-12} | {"Size",-10} | {"Current Name",-35} | {"Original Info",-25}");
        Console.WriteLine(new string('-', 88));

        foreach (var f in files)
        {
            var insp = obfuscator.Inspect(f);
            string statusStr;
            ConsoleColor col;

            switch (insp.Status)
            {
                case FileStatus.Obfuscated:
                    obfuscated++;
                    statusStr = "OBFUSCATED";
                    col = ConsoleColor.Yellow;
                    break;
                case FileStatus.Normal:
                    normal++;
                    statusStr = "Normal";
                    col = ConsoleColor.Gray;
                    break;
                default:
                    corrupted++;
                    statusStr = insp.Status.ToString().ToUpper();
                    col = ConsoleColor.Red;
                    break;
            }

            Console.ForegroundColor = col;
            Console.Write($"{statusStr,-12} ");
            Console.ResetColor();
            Console.WriteLine($"| {FormatSize(insp.FileSize),-10} | {Truncate(Path.GetFileName(f), 35),-35} | {Truncate(insp.OriginalFileName ?? "-", 25),-25}");
        }

        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"Scan Summary: Total={files.Count}, Normal={normal}, Obfuscated={obfuscated}, Other={corrupted}.");
        return 0;
    }

    private static int ExecuteSmartAction(List<string> paths, HideFilesConfig config, string? pin, string? recovery)
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

        // 如果包含已混淆文件，智能执行 unhide；否则执行 hide
        return anyObfuscated ? ExecuteUnhide(paths, false, pin, recovery) : ExecuteHide(paths, config, false);
    }

    private static int ExecuteMenu(List<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("[Error] Windows context menu is only supported on Windows.");
            return 1;
        }

        if (args.Count == 0)
        {
            Console.WriteLine($"Windows context menu is currently: {(ShellContextMenu.IsInstalled() ? "INSTALLED" : "NOT INSTALLED")}");
            Console.WriteLine("Use 'hidefiles menu install' or 'hidefiles menu uninstall'");
            return 0;
        }

        string action = args[0].ToLowerInvariant();
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "hidefiles.exe";

        if (action is "install" or "add")
        {
            ShellContextMenu.Install(currentExe, currentExe);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Success] Cascading context menu 'HideFiles' installed successfully for Current User.");
            Console.ResetColor();
            return 0;
        }
        else if (action is "uninstall" or "remove")
        {
            ShellContextMenu.Uninstall();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Success] Context menu 'HideFiles' removed successfully.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"[Error] Unknown menu action '{action}'. Choose 'install' or 'uninstall'.");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine(" HideFiles v2 - Self-Contained Encrypted Container Tool (.NET 8)");
        Console.WriteLine("==================================================================");
        Console.WriteLine("Setup:");
        Console.WriteLine("  hidefiles setpin <PIN> [--current <old-pin>]");
        Console.WriteLine("      Set or change the PIN. Changing keeps the same master key, so the");
        Console.WriteLine("      recovery code stays valid; a recovery code is printed only when the");
        Console.WriteLine("      master key is created for the first time.");
        Console.WriteLine("Usage:");
        Console.WriteLine("  hidefiles hide <paths...> [--pin <PIN>] [--dry-run]");
        Console.WriteLine("      Obfuscate via structure-lock (fast, in-place). Safe for most cases.");
        Console.WriteLine("  hidefiles encrypt <paths...> [--pin <PIN>] [--dry-run]");
        Console.WriteLine("      Full-file AES-256-GCM encryption (strongest protection).");
        Console.WriteLine("  hidefiles unhide|restore|decrypt <paths...> (--pin <PIN> | --recovery <CODE>) [--dry-run]");
        Console.WriteLine("      Restore obfuscated/encrypted files. Container mode is auto-detected.");
        Console.WriteLine("  hidefiles scan [paths...]");
        Console.WriteLine("      Inspect file status (Normal / Obfuscated-struct / Obfuscated-full).");
        Console.WriteLine("  hidefiles menu install | uninstall");
        Console.WriteLine("      Register or remove Windows cascading context menu for Current User.");
        Console.WriteLine("  hidefiles --help");
        Console.WriteLine("      Show this help information.");
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
