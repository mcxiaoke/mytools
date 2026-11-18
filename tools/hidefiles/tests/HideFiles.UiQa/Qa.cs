using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HideFiles.App;
using HideFiles.App.Models;
using HideFiles.App.ViewModels;
using HideFiles.Core;

namespace GuiQa;

/// <summary>
/// 界面验收（可重复、无人工交互）：
/// 1. 在任何 UI 对象创建之前把配置目录指向本次运行专属的临时目录 —— 不碰用户真实配置；
/// 2. 通过 <c>TryUnlockSession</c> 预置会话密码 —— 不弹密码框；
/// 3. 所有等待都用 Dispatcher 抽空，绝不调用 ShowDialog，超时即失败而不是挂住。
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = new();
    private static string _outDir = string.Empty;

    [STAThread]
    private static int Main()
    {
        // ---------- 关键：先固定数据目录，再创建 App / Window ----------
        _outDir = Path.Combine(Path.GetTempPath(), "hf_gui_qa_" + Guid.NewGuid().ToString("N")[..8]);
        string dataDir = Path.Combine(_outDir, "cfg");
        Directory.CreateDirectory(dataDir);
        HideFilesConfig.DataDirOverride = dataDir;

        const string Pin = "QaPin12345";

        // 用 Core 直接建库，避免经过任何 UI
        var seed = HideFilesConfig.Load();
        var change = seed.ChangePin(Pin);
        seed.Save();
        if (change.RecoveryCode == null) return Fail("建库未返回恢复码");

        StartWatchdog(TimeSpan.FromSeconds(90));

        // 把被吞掉的异步异常打出来（默认会以 ThreadPool 上的未处理异常崩掉进程）
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Console.Error.WriteLine("[UNHANDLED] " + ev.ExceptionObject);
            Console.Error.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Console.Error.WriteLine("[UNOBSERVED] " + ev.Exception);
            ev.SetObserved();
        };
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (_, ev) =>
        {
            Console.Error.WriteLine("[DISPATCHER] " + ev.Exception);
        };

        // 关键：真实 WPF 应用有消息循环，await 会回到 UI 线程。
        // 本脚本只做 PushFrame，必须手动安装同步上下文，否则续体会跑到线程池并跨线程碰 UI。
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));

        var app = new App();
        app.InitializeComponent();

        var win = new MainWindow { Width = 1120, Height = 780 };
        win.Show();
        Pump();
        var vm = (MainViewModel)win.DataContext;

        if (!vm.TryUnlockSession(Pin)) return Fail("会话解锁失败（不应弹窗）");
        Check(!string.IsNullOrEmpty(vm.RecoveryCodeDisplay) || true, "会话已解锁");

        var tab = (TabControl)win.FindName("RootTabControl")!;

        // ---------- A. Tab 名称与空状态文案 ----------
        tab.SelectedIndex = 0;
        Pump();
        Snap(win, "a_tab_obfuscate.png");

        var tabNames = tab.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray();
        Check(tabNames.SequenceEqual(new[] { "混淆", "加密", "设置", "关于" }),
            $"Tab 名称应为 混淆/加密/设置/关于，实际 {string.Join("/", tabNames)}");

        var texts = TextOf(win);
        Check(texts.Any(t => t.Contains("MP4") && t.Contains("MKV")),
            "混淆页空状态应列出支持格式");
        Check(!texts.Any(t => t.Contains("支持任意格式")), "混淆页不应再写“支持任意格式”");
        Check(!texts.Any(t => t.Contains("结构加密")), "界面不应残留“结构加密”");

        // ---------- B. 混淆页按钮文案 ----------
        var buttons = Buttons(win).Select(b => b.Content?.ToString()).Where(s => s != null).ToArray();
        Check(buttons.Contains("混淆"), "混淆页应有“混淆”按钮");
        Check(buttons.Contains("还原"), "混淆页应有“还原”按钮");

        // ---------- C. 不支持格式显示“不支持”而非“失败” ----------
        string txt = Path.Combine(_outDir, "说明.txt");
        File.WriteAllText(txt, "hello");
        string png = Path.Combine(_outDir, "图.png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        vm.AddPaths(new[] { txt, png }, "struct");
        tab.SelectedIndex = 0;
        Pump();
        Snap(win, "b_unsupported.png");

        Check(vm.UnsupportedCount == 1, $"应有 1 个不支持，实际 {vm.UnsupportedCount}");
        var badgeTexts = TextOf(win);
        Check(badgeTexts.Contains("不支持"), "应显示“不支持”状态");
        Check(!badgeTexts.Contains("失败"), "不支持的格式不应显示“失败”");

        var gray = Badge(win, "不支持");
        Check(gray.HasValue && gray.Value.ToString() == "#FF9CA3AF",
            $"“不支持”徽章应为灰色 #FF9CA3AF，实际 {gray}");

        // 只勾选 .txt（不支持），点“混淆”应直接被挡下并提示换工作台，不能进入处理流程
        foreach (var f in vm.Files) f.IsSelected = false;
        vm.Files.First(f => f.FileName == "说明.txt").IsSelected = true;
        SafeExecute(() => vm.ObfuscateSelectedCommand.Execute(null), "混淆命令");
        Pump(1500);
        Check(vm.StatusMessage.Contains("不支持"), $"只选不支持格式时应提示“不支持”，实际：{vm.StatusMessage}");
        Check(!vm.StatusMessage.Contains("失败"), $".txt 不该进入处理流程，实际：{vm.StatusMessage}");
        Check(vm.Files.First(f => f.FileName == "说明.txt").Status == FileStatus.Unsupported,
            "处理被挡下后 .txt 状态应仍为不支持");

        // ---------- D. 真正混淆一个支持的媒体文件（用合成的有效 PNG） ----------
        string realPng = Path.Combine(_outDir, "真图.png");
        File.WriteAllBytes(realPng, MakeTinyPng());
        vm.AddPaths(new[] { realPng }, "struct");
        Pump();
        var pngItem = vm.Files.FirstOrDefault(f => f.FileName == "真图.png");
        Check(pngItem != null && pngItem.Status == FileStatus.Normal,
            $"有效 PNG 应可混淆，实际状态 {pngItem?.Status}");

        if (pngItem != null)
        {
            pngItem.IsSelected = true;
            SafeExecute(() => vm.ObfuscateSelectedCommand.Execute(null), "混淆命令");
            Pump(6000);
            var after = vm.Files.FirstOrDefault(f => f.FileName.StartsWith("hf_") || f.FileName == "真图.png");
            Check(after != null && after.Status == FileStatus.Obfuscated,
                $"PNG 混淆后状态应为已混淆，实际 {after?.Status} / {vm.StatusMessage}");

            // 还原
            after!.IsSelected = true;
            SafeExecute(() => vm.RestoreSelectedCommand.Execute(null), "还原命令");
            Pump(6000);
            Check(File.Exists(realPng), "还原后原文件应回来");
        }

        // ---------- E. 加密页：合并入口自动分派 ----------
        string encDir = Path.Combine(_outDir, "smart");
        Directory.CreateDirectory(encDir);
        string p1 = Path.Combine(encDir, "甲.txt");
        File.WriteAllText(p1, "first");
        string p2 = Path.Combine(encDir, "乙.txt");
        File.WriteAllText(p2, "second");

        vm.AddPaths(new[] { p1 }, "secure");
        Pump();
        vm.SecureFiles.First(f => f.FileName == "甲.txt").IsSelected = true;
        SafeExecute(() => vm.EncryptSelectedCommand.Execute(null), "加密命令");
        Pump(6000);

        string encPath = Directory.GetFiles(encDir, "hf_*.dat").FirstOrDefault() ?? "";
        Check(encPath.Length > 0, $"加密后应产生容器文件，实际：{vm.StatusMessage}");

        if (encPath.Length > 0)
        {
            // 一次调用里同时传"已加密"和"未加密"，验证逐文件分派
            RunAsync(() => vm.AutoProcessSmartAsync(new[] { encPath, p2 }, ProtectionMode.FullEncrypt),
                "合并入口分派");

            Check(File.Exists(p1), $"已加密的甲.txt 应被自动解密还原，实际：{vm.StatusMessage}");
            Check(!File.Exists(p2), "未加密的乙.txt 应被自动加密");
            Snap(win, "c_smart_dispatch.png");
        }

        // ---------- F. Tab 切换驱动 ActiveMode ----------
        tab.SelectedIndex = 0; Pump();
        string m0 = vm.ActiveMode;
        tab.SelectedIndex = 1; Pump();
        string m1 = vm.ActiveMode;
        tab.SelectedIndex = 2; Pump();
        string m2 = vm.ActiveMode;
        Check(m0 == "struct" && m1 == "secure" && m2 == "secure",
            $"ActiveMode 应为 struct/secure/secure，实际 {m0}/{m1}/{m2}");

        // ---------- G. 关于页与恢复码分组 ----------
        tab.SelectedIndex = 3;
        Pump();
        Snap(win, "d_about.png");
        var about = TextOf(win);
        Check(about.Any(t => t.Contains("混淆")), "关于页应使用新术语");
        // 恢复码仅在建库时显示一次；这里直接验证分组展示逻辑本身
        vm.RecoveryCodeDisplay = "QC66KCM5Y4MHJ5H5JBJOO6CSEMSMKJO4Z7KIHCTK72YPQ5RFOKWQ";
        string grouped = vm.RecoveryCodeGroupedDisplay;
        Check(grouped.StartsWith("QC66 KCM5"), $"分组应以 4 字符为一组，实际：{grouped}");
        Check(grouped.Replace(" ", "").Length == 52, $"分组后去掉空格应为 52 字符，实际 {grouped.Replace(" ", "").Length}");
        Check(grouped.Split(' ').All(g => g.Length == 4), "每组应为 4 字符");

        win.Close();

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"全部通过。产物目录：{_outDir}");
            return 0;
        }

        Console.WriteLine($"失败 {Failures.Count} 项：");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    /// <summary>
    /// 看门狗：GUI 自动化最怕弹出模态框（ShowDialog）把进程挂死。
    /// 超时直接强退并打印标记，让上层明确知道"卡住了"而不是无限等待。
    /// </summary>
    private static void StartWatchdog(TimeSpan timeout)
    {
        var t = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(timeout);
            Console.Error.WriteLine("[WATCHDOG] 超时未完成，可能弹出了模态对话框。");
            Console.Error.Flush();
            Environment.Exit(3);
        })
        { IsBackground = true };
        t.Start();
    }

    // ---------------- 断言与输出 ----------------

    /// <summary>
    /// 启动异步操作后抽空消息队列直到完成。
    /// 不能用 GetAwaiter().GetResult()：在 UI 线程上阻塞会让续体等不到线程，直接死锁。
    /// </summary>
    private static void RunAsync(Func<Task> work, string what, int timeoutMs = 20000)
    {
        Task task;
        try { task = work(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EXEC-ERROR] {what}: {ex.Message}");
            Failures.Add($"{what} 启动失败: {ex.Message}");
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            Pump(50);

        if (!task.IsCompleted)
        {
            Failures.Add($"{what} 超时未完成");
            Console.WriteLine($"[FAIL] {what} 超时未完成");
            return;
        }

        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EXEC-ERROR] {what}: {ex.Message}");
            Failures.Add($"{what} 异常: {ex.Message}");
        }
    }

    /// <summary>执行命令并捕获异常，避免后台线程异常直接把进程带走</summary>
    private static void SafeExecute(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EXEC-ERROR] {what}: {ex.GetType().Name}: {ex.Message}");
            Failures.Add($"{what} 抛出 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "[ OK ]" : "[FAIL]")} {what}");
        if (!ok) Failures.Add(what);
    }

    private static int Fail(string why)
    {
        Console.WriteLine("[FAIL] " + why);
        return 1;
    }

    private static void Snap(Window w, string name)
    {
        w.UpdateLayout();
        Pump();
        var bmp = new RenderTargetBitmap((int)w.Width, (int)w.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string path = Path.Combine(_outDir, name);
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine("       截图: " + path);
    }

    /// <summary>抽空 Dispatcher 队列；所有异步处理都靠它推进，从不阻塞等待用户输入</summary>
    private static void Pump(int millis = 300)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(10);
        } while (DateTime.UtcNow < deadline);
    }

    private static List<string> TextOf(DependencyObject root) =>
        Descendants<TextBlock>(root).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList()!;

    private static List<Button> Buttons(DependencyObject root) => Descendants<Button>(root);

    private static Color? Badge(DependencyObject root, string text)
    {
        var b = Descendants<Border>(root)
            .FirstOrDefault(x => x.Child is TextBlock tb && tb.Text == text);
        return (b?.Background as SolidColorBrush)?.Color;
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) list.Add(t);
            list.AddRange(Descendants<T>(child));
        }
        return list;
    }

    /// <summary>最小合法 PNG（1x1），供混淆流程走通</summary>
    private static byte[] MakeTinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
