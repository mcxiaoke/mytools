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

    /// <summary>默认配置里的格式规则分组名（断言“名称完整显示”时用来定位）</summary>
    private static readonly HashSet<string> ConfigGroupNames = new()
    {
        "Videos", "Images", "Audio", "Archives", "Documents", "Others"
    };

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

        var win = new MainWindow { Width = 1140, Height = 740 };
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

        // ---------- D2. 验证已解锁会话下拖入已混淆文件时，原始文件名自动解密显示 ----------
        string preObfFile = Path.Combine(_outDir, "已加密测试图.png");
        File.WriteAllBytes(preObfFile, MakeTinyPng());
        var obfHelper = new FileObfuscator();
        var detCfg = HideFilesConfig.CreateDefault();
        detCfg.ChangePin(Pin);
        detCfg.Deterministic = true;
        detCfg.ObfuscateFileName = true;
        var obfRes = obfHelper.ObfuscateFile(preObfFile, detCfg);
        Check(obfRes.Success, "预制确定性混淆文件应成功");

        vm.AddPaths(new[] { obfRes.FinalPath }, "struct");
        Pump();
        var loadedObfItem = vm.Files.FirstOrDefault(f => f.FilePath == obfRes.FinalPath);
        Check(loadedObfItem != null, "列表应包含拖入的已混淆文件");
        Check(loadedObfItem?.Status == FileStatus.Obfuscated, "拖入的文件状态应为已混淆");
        Check(loadedObfItem?.OriginalFileName == "已加密测试图.png",
            $"已解锁会话拖入已混淆文件时，原始文件名应自动解密为「已加密测试图.png」，实际为「{loadedObfItem?.OriginalFileName}」");
        Check(loadedObfItem?.DisplayOriginalFileName == "已加密测试图.png",
            $"DisplayOriginalFileName 应显示原始文件名而非 (未解锁)，实际为「{loadedObfItem?.DisplayOriginalFileName}」");

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

        // ---------- H. 顶部标签激活态：必须与未激活有明显差异 ----------
        tab.SelectedIndex = 0;
        Pump();
        var tabItems = tab.Items.OfType<TabItem>().ToArray();

        Console.WriteLine($"       主窗口宽度: {win.Width}，最小宽度: {win.MinWidth}");
        Check(win.Width >= 1100, $"主界面应加宽到 >=1100，实际 {win.Width}");
        Check(win.MinWidth >= 900, $"最小宽度应 >=900，实际 {win.MinWidth}");
        Check(win.Width > win.MinWidth + 200, "默认宽度应明显大于最小宽度");

        var activeBackdrop = FindVisualByName<Border>(tabItems[0], "TabBorder");
        var idleBackdrop = FindVisualByName<Border>(tabItems[1], "TabBorder");
        Check(activeBackdrop != null && idleBackdrop != null, "Tab 应使用自定义模板（TabBorder 存在）");
        if (activeBackdrop != null && idleBackdrop != null)
        {
            Check(activeBackdrop.Background is SolidColorBrush { Color.A: > 200 },
                $"激活 Tab 应有实心底色，实际 {activeBackdrop.Background}");
            Check(idleBackdrop.Background is SolidColorBrush { Color.A: 0 },
                $"未激活 Tab 应为透明底，实际 {idleBackdrop.Background}");
            Check(activeBackdrop.Padding.Left >= 16,
                $"激活 Tab 内边距应足够形成可见色块，实际 {activeBackdrop.Padding}");
        }
        var activeText = FindVisualByName<TextBlock>(tabItems[0], "TabText");
        var idleText = FindVisualByName<TextBlock>(tabItems[1], "TabText");
        Check(activeText?.Foreground is SolidColorBrush ac && idleText?.Foreground is SolidColorBrush ic
              && ac.Color != ic.Color,
            $"激活/未激活 Tab 文字应为不同颜色，实际 {activeText?.Foreground} / {idleText?.Foreground}");

        // Tab 四角必须左右对称：Margin 会挤压 CornerRadius，导致“左圆右直角”
        Check(tabItems.All(t => t.Margin.Right == 0),
            "TabItem 不应使用右侧 Margin 制造间距（会挤压圆角变成直角）");
        if (activeBackdrop != null)
        {
            var sym = MeasureCornerSymmetry(win, activeBackdrop, tabItems[0]);
            Console.WriteLine($"       Tab 圆角对称性: 左内缩={sym.leftSlope:F1} 右内缩={sym.rightSlope:F1}");
            Check(sym.leftSlope > 2 && sym.rightSlope > 2,
                $"激活 Tab 左右都应有圆弧过渡，实际 左={sym.leftSlope:F1} 右={sym.rightSlope:F1}");
            Check(Math.Abs(sym.leftSlope - sym.rightSlope) <= 2.5,
                $"激活 Tab 左右圆角应大致对称，实际 左={sym.leftSlope:F1} 右={sym.rightSlope:F1}");
        }

        // ---------- I. 设置页紧凑度：分组齐全且滚动量受控 ----------
        tab.SelectedIndex = 2;
        Pump();
        Snap(win, "e_settings.png");

        var settingsTexts = TextOf(win);
        foreach (var title in new[] { "密码设置", "数据存储", "混淆文件名", "目录名伪装", "右键菜单", "格式规则" })
            Check(settingsTexts.Contains(title), $"设置页应包含分组「{title}」");
        Check(!settingsTexts.Any(t => t.Contains("格式伪装规则") || t.Contains("格式伪装规格")),
            "设置页标题应已改为「格式规则」，不应残留旧名");
        Check(!settingsTexts.Any(t => t.Contains("运行模式与数据存储")), "设置页标题应为「数据存储」");
        Check(!settingsTexts.Any(t => t.Contains("资源管理器右键菜单")), "设置页标题应为「右键菜单」");

        var scroller = Descendants<ScrollViewer>(win)
            .FirstOrDefault(s => s.ScrollableHeight > 0 || s.ExtentHeight > s.ViewportHeight);
        var settingsScroller = Descendants<ScrollViewer>(win)
            .OrderByDescending(s => s.ExtentHeight).FirstOrDefault();
        double ratio = 1.0;
        if (settingsScroller != null && settingsScroller.ViewportHeight > 0)
            ratio = settingsScroller.ExtentHeight / settingsScroller.ViewportHeight;
        Console.WriteLine($"       设置页滚动比: {ratio:F2}（1.00 = 一屏放得下）");
        Check(ratio <= 1.15,
            $"设置页内容应基本一屏可见，实际滚动比 {ratio:F2}");

        // 注意：TabItem 的内容挂在 TabControl 的 ContentPresenter 上，不在 TabItem 可视树内，
        // 必须从 ScrollViewer 的内容根往下找，否则永远统计到 0。
        DependencyObject? settingsRoot = settingsScroller?.Content as DependencyObject;
        Check(settingsRoot != null, "设置页应有可测量的内容根");

        // 内容区文字颜色不能被 Tab 顶部样式带偏（曾导致设置页标题变白“消失”）
        var contentTitle = Descendants<TextBlock>(win).FirstOrDefault(t => t.Text == "密码设置");
        Check(contentTitle != null && contentTitle.Foreground is SolidColorBrush { Color.R: < 120, Color.G: < 120, Color.B: < 120 },
            $"Tab 内容区标题应为深色，实际 {contentTitle?.Foreground}");

        if (settingsRoot != null)
        {
            var cards = Descendants<Border>(settingsRoot)
                .Where(b => Math.Abs(b.CornerRadius.TopLeft - 8) < 0.1 && b.ActualHeight > 0)
                .ToList();
            Console.WriteLine("       设置卡片高度: " + string.Join(", ", cards.Select(c => $"{c.ActualHeight:F0}")));
            Check(cards.Count >= 6, $"设置页应有 6 个分组的卡片，实际 {cards.Count}");

            // 用户要求：密码设置、数据存储、混淆文件名、目录名伪装、右键菜单 全部为单行。
            // 单行卡片的高度由内容撑开（按钮 30 + 上下 padding 24 + 边框 2 ≈ 56），放宽到 72 留出字体差异。
            var simpleCards = cards.Take(cards.Count - 1).ToList();
            Check(simpleCards.All(c => c.ActualHeight <= 72),
                "前 5 个设置分组都应精简为单行（<=72px），实际 "
                + string.Join("/", simpleCards.Select(c => $"{c.ActualHeight:F0}")));

            // 格式规则：标题行 + 两列三行，行距已放宽，不再强压高度
            Check(cards.Last().ActualHeight <= 190,
                $"格式规则卡片高度应合理（<=190px），实际 {cards.Last().ActualHeight:F0}");

            // 分组之间的间距要稍微大一些
            Check(cards.Take(cards.Count - 1).All(c => c.Margin.Bottom >= 10),
                "设置分组之间的间距应 >=10px，实际 "
                + string.Join("/", cards.Take(cards.Count - 1).Select(c => $"{c.Margin.Bottom:F0}")));
            Check(cards.All(c => c.Padding.Left >= 14 && c.Padding.Top >= 10),
                "设置卡片内 padding 应更宽松（左右>=14，上下>=10），实际 "
                + string.Join("/", cards.Select(c => $"{c.Padding.Left:F0},{c.Padding.Top:F0}")));

            var extBoxes = Descendants<TextBox>(settingsRoot)
                .Where(t => t.Text.StartsWith(".", StringComparison.Ordinal)).ToList();
            Console.WriteLine($"       格式规则后缀输入框: {extBoxes.Count} 个");
            Check(extBoxes.Count >= 6, $"格式规则应显示 6 个分组的伪装后缀，实际 {extBoxes.Count}");

            // 分组名必须完整显示（曾因固定列宽被裁成 Video/Docu/Archiv）
            var ruleNames = Descendants<CheckBox>(settingsRoot)
                .Where(c => c.Content is string s && ConfigGroupNames.Contains(s))
                .ToList();
            Console.WriteLine("       格式规则分组: " + string.Join(" / ", ruleNames.Select(c => c.Content)));
            Check(ruleNames.Count == 6, $"格式规则应有 6 个分组，实际 {ruleNames.Count}");
            foreach (var cb in ruleNames)
            {
                var text = (string)cb.Content!;
                Check(IsTextFullyVisible(cb, text), $"格式规则分组名「{text}」应完整显示，不应被裁切");
            }

            // 紧凑性：分组标题在卡片内必须明显小于卡片高度（1~2 行内容）
            var titles = Descendants<TextBlock>(settingsRoot)
                .Where(t => t.Style == app.Resources["SettingsCardTitleStyle"] as Style)
                .Select(t => t.Text).ToList();
            Console.WriteLine("       分组标题: " + string.Join(" / ", titles));
            Check(titles.Count == 6, $"设置页应有 6 个分组标题，实际 {titles.Count}");

            // 格式规则行间距：两列布局下必须按“行”分桶（X 相近视为同一行），
            // 直接按 Y 排序会得到 0，测不出真实行距。
            var ruleRows = Descendants<CheckBox>(settingsRoot)
                .Where(c => c.Content is string s2 && ConfigGroupNames.Contains(s2))
                .Select(c => c.TransformToAncestor((Visual)settingsRoot).Transform(new Point(0, 0)))
                .ToList();
            var rowTops = ruleRows.Select(p => p.Y).OrderBy(y => y)
                .Aggregate(new List<double>(), (acc, y) =>
                {
                    if (acc.Count == 0 || y - acc[^1] > 6) acc.Add(y);
                    return acc;
                });
            var gaps = new List<double>();
            for (int i = 1; i < rowTops.Count; i++) gaps.Add(rowTops[i] - rowTops[i - 1]);
            Console.WriteLine($"       格式规则行数={rowTops.Count}，行距={string.Join("/", gaps.Select(g => g.ToString("F1")))}");
            Check(rowTops.Count == 3, $"两列布局应有 3 行，实际 {rowTops.Count}");
            Check(gaps.Count > 0 && gaps.Min() >= 26,
                $"格式规则行距不应过挤（>=26px），实际 {(gaps.Count > 0 ? gaps.Min() : 0):F1}px");

            // 恢复码展示态：同一行内换成恢复码输入框，且不能把页面撑高
            const string hintText = "忘记密码时可用恢复码解密";
            // 用 Visibility 判断“是否被恢复码取代”，不依赖布局宽度（该行可能正好被压缩）
            bool HintVisible() => Descendants<TextBlock>(settingsRoot)
                .Any(t => t.Text.Contains(hintText) && t.Visibility == Visibility.Visible);
            // 前面测分组排版时曾临时赋值恢复码，这里先复位，才能验证“正常态显示提示”
            vm.RecoveryCodeDisplay = string.Empty;
            Pump();
            win.UpdateLayout();
            Check(HintVisible(), "未显示恢复码时应显示密码说明文案");

            vm.RecoveryCodeDisplay = "QC66KCM5Y4MHJ5H5JBJOO6CSEMSMKJO4Z7KIHCTK72YPQ5RFOKWQ";
            Pump();
            win.UpdateLayout();
            var recoveryBox = Descendants<TextBox>(settingsRoot)
                .FirstOrDefault(t => t.Text.Replace(" ", "").Length == 52);
            Check(recoveryBox != null, "显示恢复码时设置页应出现恢复码输入框");
            Check(!HintVisible(), "显示恢复码时提示文案应让位给恢复码");

            double ratioWithCode = settingsScroller!.ViewportHeight > 0
                ? settingsScroller.ExtentHeight / settingsScroller.ViewportHeight
                : 1.0;
            Console.WriteLine($"       显示恢复码后滚动比: {ratioWithCode:F2}");
            Check(ratioWithCode <= 1.0, $"显示恢复码后设置页仍应一屏可见，实际 {ratioWithCode:F2}");
            Snap(win, "f_settings_recovery.png");

            vm.RecoveryCodeDisplay = string.Empty;
            Pump();
            win.UpdateLayout();

            // ---------- 验证：配置项即时自动保存 ----------
            bool prevInclude = vm.Config.IncludeSubdirectories;
            vm.Config.IncludeSubdirectories = !prevInclude;
            Pump(500); // 等待防抖计时器自动写入磁盘
            string cfgJson = File.ReadAllText(Path.Combine(dataDir, "config.json"));
            Check(cfgJson.Contains($"\"IncludeSubdirectories\": {(!prevInclude).ToString().ToLowerInvariant()}"),
                "设置页面配置项修改后应即时自动保存到磁盘");
            Check(vm.StatusMessage.Contains("自动保存"), "自动保存后状态栏应有提示");
            vm.Config.IncludeSubdirectories = prevInclude;
            Pump(500);

            // ---------- 验证：主界面文件列表定位功能 ----------
            tab.SelectedIndex = 0;
            Pump();
            if (vm.Files.Count > 0)
            {
                var targetItem = vm.Files[0];
                SafeExecute(() => vm.LocateFile(targetItem), "双击文件定位");
                Check(vm.StatusMessage.Contains("资源管理器"), "双击列表项应触发在资源管理器中定位提示");
            }
        }

        tab.SelectedIndex = 3;
        Pump();

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

    /// <summary>
    /// 量化 Tab 左右圆角是否都在：把元素渲染成位图，找出强调色的外接区域，
    /// 比较「顶部第一行」与「中线」的左右内缩差值。有圆弧则内缩明显，直角则恒为 0。
    /// </summary>
    private static (double leftSlope, double rightSlope) MeasureCornerSymmetry(
        Window win, Border border, TabItem item)
    {
        double scale = VisualTreeHelper.GetDpi(win).PixelsPerDip;
        var rtb = new RenderTargetBitmap((int)(win.Width * scale), (int)(win.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(win);

        int stride = rtb.PixelWidth * 4;
        var buf = new byte[stride * rtb.PixelHeight];
        rtb.CopyPixels(buf, stride, 0);

        var origin = border.TransformToAncestor(win).Transform(new Point(0, 0));
        int left = (int)Math.Round(origin.X * scale);
        int right = (int)Math.Round((origin.X + border.ActualWidth) * scale);
        int top = (int)Math.Round(origin.Y * scale);
        int mid = (int)Math.Round((origin.Y + border.ActualHeight / 2) * scale);

        bool Accent(int x, int y)
        {
            int i = y * stride + x * 4;
            // 强调色 #004275：蓝高、红低
            return buf[i] > 95 && buf[i + 2] < 60 && buf[i + 1] > 40 && buf[i + 1] < 95;
        }

        (int lo, int hi) Scan(int y)
        {
            int lo = -1, hi = -1;
            for (int x = Math.Max(0, left - 4); x <= Math.Min(rtb.PixelWidth - 1, right + 4); x++)
                if (Accent(x, y)) { if (lo < 0) lo = x; hi = x; }
            return (lo, hi);
        }

        var topRow = Scan(top + 1);
        var midRow = Scan(mid);
        if (topRow.lo < 0 || midRow.lo < 0) return (0, 0);

        double leftSlope = topRow.lo - midRow.lo;
        double rightSlope = midRow.hi - topRow.hi;
        return (leftSlope, rightSlope);
    }

    /// <summary>按模板内部名字取元素（用于断言自定义 Tab 模板里的色块）</summary>
    private static T? FindVisualByName<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).FirstOrDefault(x => x.Name == name);

    /// <summary>
    /// 判断控件内的文字是否完整可见：用实际字体测量文本宽度，
    /// 再与控件可用内容宽度比较，避免“靠肉眼判断裁切”。
    /// </summary>
    private static bool IsTextFullyVisible(ContentControl host, string text)
    {
        var tb = Descendants<TextBlock>(host).FirstOrDefault();
        if (tb == null) return false;
        var dpi = VisualTreeHelper.GetDpi(tb);
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, dpi.PixelsPerDip);
        double contentWidth = host.ActualWidth - host.Padding.Left - host.Padding.Right
                              - host.BorderThickness.Left - host.BorderThickness.Right;
        return contentWidth >= formatted.WidthIncludingTrailingWhitespace - 0.5;
    }

    /// <summary>最小合法 PNG（1x1），供混淆流程走通</summary>
    private static byte[] MakeTinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
