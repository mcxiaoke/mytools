using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HideFiles.App.Models;
using HideFiles.App.Views;
using HideFiles.Core;
using Microsoft.Win32;

namespace HideFiles.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileObfuscator _obfuscator = new();
    /// <summary>本次会话验证通过的 PIN（v2 解密用明文 PIN，不落盘）</summary>
    private string? _sessionPin;
    /// <summary>本次会话验证通过的恢复码（与 PIN 二选一，均可用于还原）</summary>
    private string? _sessionRecoveryCode;
    /// <summary>本机 config 中当前 PIN 对应的主密钥（改过 PIN 时兜底，避免每个文件都跑一次 PBKDF2）</summary>
    private byte[]? _sessionMasterKey;
    private CancellationTokenSource? _processCts;

    [ObservableProperty]
    private string _title = $"{AppBrand.Name} - {AppBrand.Tagline}";

    [ObservableProperty]
    private HideFilesConfig _config;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "就绪。支持拖拽文件或文件夹到窗口。";

    [ObservableProperty]
    private bool _isContextMenuInstalled;

    [ObservableProperty]
    private string _newPinInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryCode))]
    [NotifyPropertyChangedFor(nameof(RecoveryCodeGroupedDisplay))]
    private string _recoveryCodeDisplay = string.Empty;

    public bool HasRecoveryCode => !string.IsNullOrEmpty(RecoveryCodeDisplay);

    /// <summary>恢复码每 4 字符分组展示，便于人工抄写（解码端已兼容空格/横线）</summary>
    public string RecoveryCodeGroupedDisplay =>
        string.IsNullOrEmpty(RecoveryCodeDisplay)
            ? string.Empty
            : string.Join(" ", Enumerable.Range(0, (RecoveryCodeDisplay.Length + 3) / 4)
                .Select(i => RecoveryCodeDisplay.Substring(i * 4, Math.Min(4, RecoveryCodeDisplay.Length - i * 4))));

    /// <summary>最近一次批量处理的失败清单（每行一条：“文件名: 原因”），无失败时为 null</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailureReport))]
    private string? _lastFailureReport;

    public bool HasFailureReport => !string.IsNullOrEmpty(LastFailureReport);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfiguredPinDisplay))]
    private bool _hasPinConfigured;

    public string ConfiguredPinDisplay => HasPinConfigured ? "已设置 (●●●●●●)" : "未设置";

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>当前活动工作台：struct=混淆 secure=加密（由 Tab 切换设置，决定拖拽/外部投递目标）</summary>
    [ObservableProperty]
    private string _activeMode = "struct";

    /// <summary>
    /// Tab 切换时同步活动工作台。索引 0=混淆、1=加密；
    /// 设置页/关于页（2、3）不是工作台，保持上一个工作台不变。
    /// </summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 0) ActiveMode = "struct";
        else if (value == 1) ActiveMode = "secure";
    }

    /// <summary>混淆工作台文件列表</summary>
    public ObservableRangeCollection<FileItemModel> Files { get; } = new();
    /// <summary>加密工作台文件列表</summary>
    public ObservableRangeCollection<FileItemModel> SecureFiles { get; } = new();

    public int TotalCount => Files.Count;
    public int NormalCount => Files.Count(f => f.Status == FileStatus.Normal);
    public int ObfuscatedCount => Files.Count(f => f.Status == FileStatus.Obfuscated);
    /// <summary>格式不受混淆支持的数量（不是失败，只是需要换模式）</summary>
    public int UnsupportedCount => Files.Count(f => f.Status == FileStatus.Unsupported);
    public bool HasUnsupported => UnsupportedCount > 0;
    public bool HasFiles => TotalCount > 0;

    public int SecureTotalCount => SecureFiles.Count;
    public int SecureNormalCount => SecureFiles.Count(f => f.Status == FileStatus.Normal);
    public int SecureObfuscatedCount => SecureFiles.Count(f => f.Status == FileStatus.Obfuscated);
    public bool HasSecureFiles => SecureTotalCount > 0;

    public MainViewModel()
    {
        _config = HideFilesConfig.Load();
        HasPinConfigured = _config.HasPin;
        RefreshContextMenuStatus();

        // 配置损坏时是静默回退默认值的：此时密钥材料已从内存消失，
        // 若用户直接重设密码会生成新主密钥，导致历史文件用新密码永久打不开。
        // 必须显式告知用户备份位置与恢复方式。
        if (HideFilesConfig.LastLoadWasCorrupted)
        {
            string backup = HideFilesConfig.LastLoadBackupPath ?? "(备份失败)";
            StatusMessage = $"配置文件已损坏，已回退默认设置。原文件备份在：{backup}。"
                          + "请勿直接重设密码——先用备份文件恢复 config.json，否则历史文件只能用恢复码解密。";
            _configCorruptedOnLoad = true;
        }
    }

    /// <summary>本次启动时配置文件损坏（用于在设置密码前拦截）</summary>
    private readonly bool _configCorruptedOnLoad;

    private ObservableRangeCollection<FileItemModel> CollectionFor(string mode)
        => mode == "secure" ? SecureFiles : Files;

    public void RefreshContextMenuStatus()
    {
        if (OperatingSystem.IsWindows())
            IsContextMenuInstalled = ShellContextMenu.IsInstalled();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(NormalCount));
        OnPropertyChanged(nameof(ObfuscatedCount));
        OnPropertyChanged(nameof(UnsupportedCount));
        OnPropertyChanged(nameof(HasUnsupported));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(SecureTotalCount));
        OnPropertyChanged(nameof(SecureNormalCount));
        OnPropertyChanged(nameof(SecureObfuscatedCount));
        OnPropertyChanged(nameof(HasSecureFiles));
    }

    /// <summary>添加路径到指定工作台（mode: struct/secure）</summary>
    public void AddPaths(IEnumerable<string> paths, string? mode = null)
    {
        _ = AddPathsAsync(paths, mode);
    }

    /// <summary>异步添加路径到指定工作台，支持深层目录扫描进度展示与并发元数据分析</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths, string? mode = null)
    {
        var pathList = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (pathList == null || pathList.Count == 0) return;

        var collection = CollectionFor(mode ?? ActiveMode);
        var existing = new HashSet<string>(collection.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
        bool structure = ReferenceEquals(collection, Files);

        // 如果存在目录或批量文件大于等于 20 个，且主窗口可见，则弹出扫描进度对话框
        bool hasDirectory = pathList.Any(Directory.Exists);
        bool shouldShowDialog = hasDirectory || pathList.Count >= 20;

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        ScanProgressDialog? dialog = null;

        if (shouldShowDialog && Application.Current?.MainWindow is Window mainWindow && mainWindow.IsVisible)
        {
            dialog = new ScanProgressDialog(cts) { Owner = mainWindow };
        }

        var newItems = new System.Collections.Concurrent.ConcurrentBag<FileItemModel>();
        bool canceled = false;

        Task workerTask = Task.Run(async () =>
        {
            try
            {
                // 阶段 1：枚举文件
                var candidateFiles = new List<string>();
                var opt = new EnumerationOptions
                {
                    RecurseSubdirectories = Config.IncludeSubdirectories,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                long lastReportTick = 0;
                foreach (var p in pathList)
                {
                    if (token.IsCancellationRequested) break;
                    if (File.Exists(p))
                    {
                        string full = Path.GetFullPath(p);
                        if (!existing.Contains(full))
                        {
                            candidateFiles.Add(full);
                        }
                    }
                    else if (Directory.Exists(p))
                    {
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(p, "*.*", opt))
                            {
                                if (token.IsCancellationRequested) break;
                                string full = Path.GetFullPath(f);
                                if (!existing.Contains(full))
                                {
                                    candidateFiles.Add(full);
                                }

                                long now = Environment.TickCount64;
                                if (now - lastReportTick > 100)
                                {
                                    lastReportTick = now;
                                    dialog?.ReportSearch(candidateFiles.Count, Path.GetDirectoryName(full) ?? full);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Enumerate files error: {ex.Message}");
                        }
                    }
                }

                if (token.IsCancellationRequested)
                {
                    canceled = true;
                    return;
                }

                int totalCandidates = candidateFiles.Count;
                if (totalCandidates == 0) return;

                // 阶段 2：并发 Inspect
                int processedCount = 0;
                int maxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency,
                    CancellationToken = token
                };

                await Parallel.ForEachAsync(candidateFiles, parallelOptions, (file, ct) =>
                {
                    if (ct.IsCancellationRequested) return ValueTask.CompletedTask;

                    var item = InspectSingleFile(file, structure);
                    newItems.Add(item);

                    int current = Interlocked.Increment(ref processedCount);
                    long now = Environment.TickCount64;
                    if (now - lastReportTick > 80 || current == totalCandidates)
                    {
                        lastReportTick = now;
                        dialog?.ReportInspect(current, totalCandidates, item.FileName);
                    }

                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Scan files error: {ex.Message}");
            }
            finally
            {
                dialog?.CloseDialog();
            }
        });

        if (dialog != null)
        {
            dialog.ShowDialog();
        }

        await workerTask;

        // 阶段 3：批量回填 UI
        if (!newItems.IsEmpty)
        {
            var ordered = newItems.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            collection.AddRange(ordered);
        }

        UpdateCounts();
        if (canceled)
        {
            StatusMessage = $"扫描已取消，保留已加载的 {newItems.Count} 个文件。";
        }
        else
        {
            StatusMessage = DescribeLoad(collection, structure);
        }
    }

    /// <summary>加载完成后的状态摘要；混淆工作台会把"不支持"单列，不计入未加密</summary>
    private static string DescribeLoad(ObservableCollection<FileItemModel> collection, bool structure)
    {
        int total = collection.Count;
        int obfuscated = collection.Count(f => f.Status == FileStatus.Obfuscated);

        if (!structure)
            return $"已加载 {total} 个文件 (未加密: {total - obfuscated}, 已加密: {obfuscated})。";

        int normal = collection.Count(f => f.Status == FileStatus.Normal);
        int unsupported = collection.Count(f => f.Status == FileStatus.Unsupported);
        string text = $"已加载 {total} 个文件（可混淆 {normal}，已混淆 {obfuscated}";
        if (unsupported > 0)
            text += $"，不支持 {unsupported} 个（请改用“加密”工作台）";
        return text + "）。";
    }

    private FileItemModel InspectSingleFile(string full, bool structureWorkspace)
    {
        var insp = _obfuscator.Inspect(full, _sessionMasterKey, forStructureWorkspace: structureWorkspace);
        return new FileItemModel
        {
            FilePath = full,
            FileName = Path.GetFileName(full),
            FileSize = insp.FileSize,
            Status = insp.Status,
            OriginalFileName = insp.OriginalFileName,
            StatusText = insp.Status switch
            {
                FileStatus.Obfuscated => "已混淆",
                FileStatus.Normal => "未混淆",
                FileStatus.Unsupported => "不支持",
                FileStatus.Corrupted => "损坏",
                FileStatus.Locked => "已占用",
                _ => "未知"
            },
            ErrorMessage = insp.Status == FileStatus.Unsupported ? insp.ErrorMessage : null
        };
    }

    [RelayCommand]
    private async Task SelectFiles(string mode)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要处理的文件" };
        if (dlg.ShowDialog() == true) await AddPathsAsync(dlg.FileNames, mode);
    }

    [RelayCommand]
    private async Task SelectFolder(string mode)
    {
        var dlg = new OpenFolderDialog { Title = "选择包含目标文件的文件夹", Multiselect = false };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            await AddPathsAsync(new[] { dlg.FolderName }, mode);
    }

    [RelayCommand]
    private void ClearFiles(string mode)
    {
        CollectionFor(mode).Clear();
        UpdateCounts();
        ProgressValue = 0;
        StatusMessage = "列表已清空。";
    }

    [RelayCommand]
    private void ToggleSelectAll(string mode)
    {
        var collection = CollectionFor(mode);
        bool allSelected = collection.Count > 0 && collection.All(f => f.IsSelected);
        foreach (var item in collection) item.IsSelected = !allSelected;
    }

    [RelayCommand]
    private void RemoveFile(FileItemModel? item)
    {
        if (item == null) return;
        Files.Remove(item);
        SecureFiles.Remove(item);
        UpdateCounts();
    }

    [RelayCommand]
    private void CopyRecoveryCode()
    {
        if (string.IsNullOrEmpty(RecoveryCodeDisplay)) return;

        // 恢复码等同主密钥：尽量不进 Win11 剪贴板历史/云同步，并在 60 秒后自动清空
        if (!TrySetClipboardSensitive(RecoveryCodeDisplay))
        {
            try { Clipboard.SetText(RecoveryCodeDisplay); }
            catch (Exception ex) { StatusMessage = $"复制恢复码失败: {ex.Message}"; return; }
        }

        StatusMessage = "恢复码已复制到剪贴板，60 秒后自动清空。";
        ScheduleClipboardClear(RecoveryCodeDisplay);
    }

    /// <summary>
    /// 写入剪贴板并标记为敏感内容，阻止进入剪贴板历史与云同步。
    /// 这些是 Windows 10 1809+ 约定的剪贴板格式；系统不认识时会被忽略，不影响文本本身。
    /// 返回 false 表示该路径不可用，调用方应退回普通 SetText。
    /// </summary>
    private static bool TrySetClipboardSensitive(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", DwordBytes(1));
            data.SetData("CanIncludeInClipboardHistory", DwordBytes(0));
            data.SetData("CanUploadToCloudClipboard", DwordBytes(0));
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch
        {
            return false;   // 保持普通剪贴板行为，不让密码复制失败
        }

        static byte[] DwordBytes(int value) => BitConverter.GetBytes(value);
    }

    /// <summary>60 秒后若剪贴板仍是这段恢复码则清空，避免覆盖用户后来复制的内容。</summary>
    private void ScheduleClipboardClear(string expected)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            try
            {
                dispatcher.Invoke(() =>
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == expected)
                    {
                        Clipboard.Clear();
                        StatusMessage = "恢复码已从剪贴板自动清空。";
                    }
                });
            }
            catch
            {
                // 应用正在退出或剪贴板被其它进程占用：忽略即可
            }
        });
    }

    /// <summary>把最近一次批量处理的失败清单导出为 txt，便于排查长列表失败原因</summary>
    [RelayCommand]
    private void ExportFailureReport()
    {
        if (string.IsNullOrEmpty(LastFailureReport))
        {
            StatusMessage = "本次没有失败记录可导出。";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出失败清单",
            FileName = $"{AppBrand.Name}-失败清单-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName,
                $"{AppBrand.Name} 失败清单  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                new string('-', 60) + Environment.NewLine +
                LastFailureReport + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusMessage = $"失败清单已导出：{dlg.FileName}";
        }
        catch (Exception ex) { StatusMessage = $"导出失败清单失败: {ex.Message}"; }
    }

    /// <summary>部署模式与数据目录（支持标准漫游与便携模式）</summary>
    public string DeploymentModeDisplay => HideFilesConfig.DeploymentModeName;
    public string DataDirectoryDisplay => HideFilesConfig.DirPath;
    public bool IsPortableMode => HideFilesConfig.IsPortableMode;

    /// <summary>关于界面展示信息</summary>
    public string AppVersionDisplay => $"{AppBrand.Name} v{BuildInfo.Version}";
    public string GitHashDisplay => BuildInfo.GitHash;
    public string BuildTimeDisplay => BuildInfo.BuildTime;

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            string dir = HideFilesConfig.DirPath;
            if (Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    /// <summary>弹出安全验证对话框，记录本会话凭据（allowRecovery 控制是否允许使用恢复码）</summary>
    /// <summary>
    /// 直接用已有密码解锁会话，不弹窗。
    /// 供自动化测试与将来的"记住密码/静默解锁"使用；密码错误时返回 false 且不改动会话。
    /// </summary>
    public bool TryUnlockSession(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;

        if (Config.HasKeyMaterial)
        {
            try { Config.UnwrapMasterKeyWithPin(pin); }
            catch { return false; }
        }
        else if (!Config.VerifyPin(pin))
        {
            return false;
        }

        _sessionPin = pin.Trim();
        _sessionRecoveryCode = null;
        Config.SessionPin = _sessionPin;
        RefreshSessionMasterKey();
        return true;
    }

    /// <summary>用恢复码解锁会话，不弹窗。</summary>
    public bool TryUnlockSessionWithRecoveryCode(string recoveryCode)
    {
        try { V2Crypto.RecoveryCodeToMasterKey(recoveryCode); }
        catch (FormatException) { return false; }

        _sessionRecoveryCode = recoveryCode;
        _sessionPin = null;
        Config.SessionPin = null;
        RefreshSessionMasterKey();
        return true;
    }

    private bool TryPromptCredential(bool allowRecovery = true)
    {
        var dlg = new Views.PasswordDialog(Config, Views.PasswordDialogMode.Verify, allowRecovery: allowRecovery)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || !dlg.IsVerified) return false;

        _sessionPin = string.IsNullOrEmpty(dlg.EnteredPin) ? null : dlg.EnteredPin;
        _sessionRecoveryCode = string.IsNullOrEmpty(dlg.EnteredRecoveryCode) ? null : dlg.EnteredRecoveryCode;
        Config.SessionPin = _sessionPin;
        RefreshSessionMasterKey();
        return true;
    }

    /// <summary>用当前会话 PIN 解出本机主密钥并缓存（PBKDF2 只跑一次，批量还原不必逐文件派生）</summary>
    private void RefreshSessionMasterKey()
    {
        _sessionMasterKey = null;
        if (string.IsNullOrEmpty(_sessionPin) || !Config.HasKeyMaterial) return;
        try { _sessionMasterKey = Config.UnwrapMasterKeyWithPin(_sessionPin); }
        catch { _sessionMasterKey = null; }
    }

    /// <summary>
    /// 还原所需凭据：密码或恢复码任一即可。
    /// 即使本机已没有密钥材料（密码被清除），也允许用恢复码还原。
    /// </summary>
    public bool EnsureRestoreCredential()
    {
        if (!string.IsNullOrEmpty(_sessionPin) || !string.IsNullOrEmpty(_sessionRecoveryCode)) return true;
        return TryPromptCredential(allowRecovery: true);
    }

    /// <summary>管理类操作（清除/修改密码）的身份验证：未设置密码时无需验证</summary>
    public bool EnsurePinVerified()
    {
        if (!Config.HasPin) return true;
        if (!string.IsNullOrEmpty(_sessionPin) && Config.VerifyPin(_sessionPin)) return true;
        return TryPromptCredential(allowRecovery: false);
    }

    [RelayCommand]
    private void PromptSetPin()
    {
        // 配置损坏后重设密码 = 生成新主密钥，历史文件用新密码再也打不开。
        // 这是不可逆操作，必须先让用户确认已知情。
        if (_configCorruptedOnLoad)
        {
            var warn = MessageBox.Show(
                "本次启动时配置文件已损坏并回退为默认设置，原有主密钥不在内存中。\n\n" +
                "现在设置密码会生成全新的主密钥，此前加密的文件将无法用这个新密码解密，" +
                "只能靠原恢复码还原。\n\n确认继续设置新密码吗？",
                "配置已损坏 — 谨慎继续", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (warn != MessageBoxResult.OK) return;
        }

        var dlg = new Views.PasswordDialog(Config, Views.PasswordDialogMode.Set)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || !dlg.IsVerified) return;

        var result = Config.ChangePin(dlg.EnteredPin, currentPin: null);
        if (!result.Success)
        {
            StatusMessage = result.ErrorMessage ?? "设置密码失败。";
            return;
        }

        Config.Save();
        HasPinConfigured = Config.HasPin;
        _sessionPin = Config.SessionPin;
        RefreshSessionMasterKey();
        if (result.MasterKeyRecreated)
        {
            RecoveryCodeDisplay = result.RecoveryCode ?? string.Empty;
            StatusMessage = "主密码已设置成功，请妥善保存恢复码（仅显示一次）。";
        }
        else
        {
            StatusMessage = "主密码已设置成功。";
        }
    }

    [RelayCommand]
    private void PromptChangePin()
    {
        if (!Config.HasPin)
        {
            PromptSetPin();
            return;
        }

        var dlg = new Views.PasswordDialog(Config, Views.PasswordDialogMode.Change)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || !dlg.IsVerified) return;

        var result = Config.ChangePin(dlg.EnteredPin, dlg.CurrentPin);
        if (!result.Success)
        {
            StatusMessage = result.ErrorMessage ?? "修改密码失败。";
            return;
        }

        Config.Save();
        HasPinConfigured = Config.HasPin;
        _sessionPin = Config.SessionPin;
        RefreshSessionMasterKey();
        StatusMessage = "主密码修改成功。历史文件仍可使用新密码正常解密。";
    }

    [RelayCommand]
    private void PromptClearPin()
    {
        if (!Config.HasPin) return;

        var dlg = new Views.PasswordDialog(Config, Views.PasswordDialogMode.Verify)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || !dlg.IsVerified)
        {
            StatusMessage = "验证未通过，密码未清除。";
            return;
        }

        var confirm = MessageBox.Show(
            "清除密码将删除本机保存的密钥材料，此前加密的文件将只能通过恢复码解密。\n\n确定要清除当前密码吗？",
            "清除密码确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        Config.ClearKeyMaterial();
        Config.Save();
        HasPinConfigured = false;
        _sessionPin = null;
        _sessionRecoveryCode = null;
        _sessionMasterKey = null;
        RecoveryCodeDisplay = string.Empty;
        StatusMessage = "主密码已清除。历史加密文件需使用恢复码解密。";
    }

    /// <summary>确保加密所需的密码会话已解锁（加密强制需要密码，禁用恢复码）</summary>
    private bool EnsureEncryptionReady(string action)
    {
        if (!Config.HasPin || !Config.HasKeyMaterial)
        {
            var setDlg = new Views.PasswordDialog(Config, Views.PasswordDialogMode.Set)
            {
                Owner = Application.Current?.MainWindow
            };
            if (setDlg.ShowDialog() != true || !setDlg.IsVerified)
            {
                StatusMessage = $"未设置密码，{action}已中止。";
                return false;
            }

            var result = Config.ChangePin(setDlg.EnteredPin, currentPin: null);
            if (!result.Success)
            {
                StatusMessage = result.ErrorMessage ?? "设置密码失败。";
                return false;
            }

            Config.Save();
            HasPinConfigured = Config.HasPin;
            _sessionPin = Config.SessionPin;
            RefreshSessionMasterKey();
            if (result.MasterKeyRecreated)
                RecoveryCodeDisplay = result.RecoveryCode ?? string.Empty;

            StatusMessage = "主密码已设置，请在设置中查看并保存恢复码。";
        }

        if (!string.IsNullOrEmpty(Config.SessionPin) && Config.VerifyPin(Config.SessionPin))
            return true;

        if (!TryPromptCredential(allowRecovery: false) || string.IsNullOrEmpty(Config.SessionPin))
        {
            StatusMessage = $"需要输入密码解锁，{action}已中止。";
            return false;
        }

        return true;
    }

    // ---------- 混淆工作台 ----------
    [RelayCommand]
    private async Task ObfuscateSelectedAsync()
        => await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: true);

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (!EnsureRestoreCredential()) { StatusMessage = "未提供密码或恢复码，解密已中止。"; return; }
        await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: false);
    }

    // ---------- 加密工作台 ----------
    [RelayCommand]
    private async Task EncryptSelectedAsync()
        => await ProcessSelectedAsync(SecureFiles, ProtectionMode.FullEncrypt, isObfuscate: true);

    [RelayCommand]
    private async Task DecryptSelectedAsync()
    {
        if (!EnsureRestoreCredential()) { StatusMessage = "未提供密码或恢复码，解密已中止。"; return; }
        await ProcessSelectedAsync(SecureFiles, ProtectionMode.FullEncrypt, isObfuscate: false);
    }

    private async Task ProcessSelectedAsync(ObservableCollection<FileItemModel> collection,
        ProtectionMode mode, bool isObfuscate)
    {
        var targets = collection.Where(f => f.IsSelected &&
            (isObfuscate ? f.Status == FileStatus.Normal : f.Status == FileStatus.Obfuscated)).ToList();
        if (targets.Count == 0)
        {
            int unsupported = collection.Count(f => f.IsSelected && f.Status == FileStatus.Unsupported);
            StatusMessage = isObfuscate
                ? (unsupported > 0
                    ? $"选中的 {unsupported} 个文件格式不支持混淆，请改用“加密”工作台。"
                    : "未选择未混淆的文件。")
                : "未选择已混淆的文件。";
            return;
        }
        if (isObfuscate && !EnsureEncryptionReady(mode == ProtectionMode.FullEncrypt ? "加密" : "加密")) return;

        await ProcessAsync(targets, isObfuscate, mode);
    }

    /// <summary>供右键菜单/启动参数调用：自动处理路径（按模式选集合与操作）</summary>
    public async Task AutoProcessPathsAsync(IEnumerable<string> paths, ProtectionMode mode, bool isObfuscate)
    {
        string m = mode == ProtectionMode.FullEncrypt ? "secure" : "struct";
        await AddPathsAsync(paths, m);
        var collection = CollectionFor(m);
        if (isObfuscate && !EnsureEncryptionReady(mode == ProtectionMode.FullEncrypt ? "加密" : "加密")) return;
        if (!isObfuscate && !EnsureRestoreCredential()) return;
        await ProcessAsync(collection.Where(f =>
            (isObfuscate ? f.Status == FileStatus.Normal : f.Status == FileStatus.Obfuscated)).ToList(), isObfuscate, mode);
    }

    /// <summary>
    /// 供新的右键菜单调用：一个入口同时覆盖"做"与"还原"。
    /// 按每个文件的实际容器状态分派方向：未加密 → 加密/混淆，已加密 → 解密/还原。
    /// 目标集合固定为指定工作台，避免"简单动词"带来的模式错配。
    /// </summary>
    public async Task AutoProcessSmartAsync(IEnumerable<string> paths, ProtectionMode mode)
    {
        string m = mode == ProtectionMode.FullEncrypt ? "secure" : "struct";
        bool structure = mode == ProtectionMode.StructureLock;
        await AddPathsAsync(paths, m);
        var collection = CollectionFor(m);

        var toProtect = collection
            .Where(f => f.Status == FileStatus.Normal && f.IsSelected).ToList();
        var toRestore = collection
            .Where(f => f.Status == FileStatus.Obfuscated && f.IsSelected).ToList();

        string verb = structure ? "混淆" : "加密";
        string undo = structure ? "还原" : "解密";

        if (toProtect.Count == 0 && toRestore.Count == 0)
        {
            int unsupported = collection.Count(f => f.Status == FileStatus.Unsupported);
            StatusMessage = unsupported > 0
                ? $"选中的 {unsupported} 个文件格式不支持混淆，请改用“加密”工作台。"
                : "没有可处理的文件（已全部处理或处于其它状态）。";
            return;
        }

        if (toProtect.Count > 0)
        {
            if (!EnsureEncryptionReady(verb)) return;
            await ProcessAsync(toProtect, isObfuscate: true, mode);
        }

        if (toRestore.Count > 0)
        {
            if (!EnsureRestoreCredential()) return;
            await ProcessAsync(toRestore, isObfuscate: false, mode);
        }
    }

    [RelayCommand]
    private void CancelProcessing()
    {
        if (IsProcessing && _processCts != null && !_processCts.IsCancellationRequested)
        {
            _processCts.Cancel();
            StatusMessage = "正在取消操作...";
        }
    }

    private async Task ProcessAsync(List<FileItemModel> targetItems, bool isObfuscate,
        ProtectionMode? modeOverride = null)
    {
        // 防重入：批量任务串行执行，避免两次点击同时改写 Config.ProtectionMode 与同一批文件
        if (IsProcessing) { StatusMessage = "正在处理任务，请等待完成。"; return; }

        var prevMode = Config.ProtectionMode;
        if (modeOverride.HasValue) Config.ProtectionMode = modeOverride.Value;
        IsProcessing = true;
        ProgressValue = 0;
        int total = targetItems.Count;
        int successCount = 0;
        int unsupportedCount = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        bool canceled = false;

        _processCts = new CancellationTokenSource();
        var token = _processCts.Token;

        var dispatcher = Application.Current?.Dispatcher;

        bool structWorkspace = (modeOverride ?? Config.ProtectionMode) == ProtectionMode.StructureLock;
        string actionVerb = isObfuscate
            ? (structWorkspace ? "混淆" : "加密")
            : (structWorkspace ? "还原" : "解密");
        string actionTitle = $"正在{actionVerb}文件...";

        // 大于等于 10 个文件且主窗口可见时，弹出模态处理进度对话框
        bool shouldShowDialog = total >= 10 && Application.Current?.MainWindow is Window mw && mw.IsVisible;
        OperationProgressDialog? dialog = null;
        if (shouldShowDialog && Application.Current?.MainWindow is Window mainWindow)
        {
            dialog = new OperationProgressDialog(actionTitle, _processCts) { Owner = mainWindow };
        }

        try
        {
            int maxConcurrency = total <= 3 ? 1 : (structWorkspace ? 4 : Math.Clamp(Environment.ProcessorCount, 2, 8));
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = token
            };

            int processedCount = 0;
            long lastReportTick = 0;

            Task workerTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(targetItems, parallelOptions, (item, ct) =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            canceled = true;
                            return ValueTask.CompletedTask;
                        }

                        ProcessFileResult res = isObfuscate
                            ? _obfuscator.ObfuscateFile(item.FilePath, Config, ct)
                            : _obfuscator.RestoreFile(item.FilePath, _sessionPin, _sessionRecoveryCode, modeOverride ?? Config.ProtectionMode, ct);

                        if (ct.IsCancellationRequested || res.ErrorKind == ProcessErrorKind.Canceled)
                        {
                            canceled = true;
                            return ValueTask.CompletedTask;
                        }

                        if (res.Success)
                        {
                            Interlocked.Increment(ref successCount);
                            var insp = _obfuscator.Inspect(res.FinalPath, _sessionMasterKey, structWorkspace);
                            dispatcher?.InvokeAsync(() =>
                            {
                                item.FilePath = res.FinalPath;
                                item.FileName = Path.GetFileName(res.FinalPath);
                                item.Status = insp.Status;
                                item.OriginalFileName = insp.OriginalFileName;
                                item.ErrorMessage = null;
                                item.StatusText = isObfuscate
                                    ? (structWorkspace ? "已混淆" : "已加密")
                                    : (structWorkspace ? "未混淆" : "未加密");
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                        else if (res.ErrorKind == ProcessErrorKind.UnsupportedFormat)
                        {
                            Interlocked.Increment(ref unsupportedCount);
                            dispatcher?.InvokeAsync(() =>
                            {
                                item.Status = FileStatus.Unsupported;
                                item.StatusText = "不支持";
                                item.ErrorMessage = res.ErrorMessage;
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                        else
                        {
                            string reason = string.IsNullOrWhiteSpace(res.ErrorMessage ?? null)
                                ? "未知错误"
                                : res.ErrorMessage!;
                            failures.Add($"{item.FileName}: {reason}");
                            dispatcher?.InvokeAsync(() =>
                            {
                                item.ErrorMessage = reason;
                                item.StatusText = "失败";
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }

                        int current = Interlocked.Increment(ref processedCount);
                        long now = Environment.TickCount64;
                        if ((now - lastReportTick > 80 && current < total) || current == total)
                        {
                            lastReportTick = now;
                            double pct = (double)current / total * 100.0;
                            if (current < total)
                            {
                                dispatcher?.InvokeAsync(() =>
                                {
                                    ProgressValue = pct;
                                    StatusMessage = $"正在处理 ({current}/{total}): {item.FileName}";
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }

                            dialog?.ReportProgress(current, total, successCount, failures.Count, item.FileName);
                        }

                        return ValueTask.CompletedTask;
                    });
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ProcessAsync error: {ex.Message}");
                }
                finally
                {
                    dialog?.CloseDialog();
                }
            });

            if (dialog != null)
            {
                dialog.ShowDialog();
            }

            await workerTask;
        }
        finally
        {
            _processCts?.Dispose();
            _processCts = null;
            Config.ProtectionMode = prevMode;
            IsProcessing = false;
        }

        UpdateCounts();
        LastFailureReport = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : null;

        string unsupportedNote = unsupportedCount > 0
            ? $"，{unsupportedCount} 个格式不支持请改用“加密”工作台"
            : string.Empty;

        if (canceled)
        {
            StatusMessage = $"操作已取消：已完成 {successCount}/{total}{unsupportedNote}。";
        }
        else
        {
            StatusMessage = failures.Count == 0
                ? $"处理完成：成功 {successCount}/{total}{unsupportedNote}。"
                : $"处理完成：成功 {successCount}/{total}，失败 {failures.Count}{unsupportedNote}（失败原因见列表“状态”列，可点“导出失败清单”）";
        }
    }


    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            Config.Save();
            StatusMessage = "设置已保存。";
        }
        catch (Exception ex) { StatusMessage = $"保存设置失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void ResetConfig()
    {
        // 只重置规则，保留密钥材料
        var fresh = HideFilesConfig.CreateDefault();
        fresh.ConfigSalt = Config.ConfigSalt;
        fresh.WrappedMasterKey = Config.WrappedMasterKey;
        fresh.RecoveryCodeHash = Config.RecoveryCodeHash;
        fresh.KdfIterations = Config.KdfIterations;
        fresh.SessionPin = Config.SessionPin;

        Config = fresh;
        Config.Save();
        HasPinConfigured = Config.HasPin;
        RefreshSessionMasterKey();
        StatusMessage = "已恢复默认设置（密码保持不变）。";
    }

    // ---------- 目录名伪装（独立操作） ----------
    [RelayCommand]
    private void ObfuscateDirNames() => ApplyDirectoryNames(obfuscate: true);

    [RelayCommand]
    private void RestoreDirNames() => ApplyDirectoryNames(obfuscate: false);

    private void ApplyDirectoryNames(bool obfuscate)
    {
        var dlg = new OpenFolderDialog { Title = obfuscate ? "选择要伪装子目录名的文件夹" : "选择要还原子目录名的文件夹" };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FolderName)) return;

        var result = obfuscate
            ? DirectoryObfuscator.ObfuscateDirectory(dlg.FolderName)
            : DirectoryObfuscator.RestoreDirectory(dlg.FolderName);

        string verb = obfuscate ? "伪装" : "还原";
        StatusMessage = result.Success
            ? $"目录名{verb}完成：改名 {result.Renamed}，跳过 {result.Skipped}。"
            : $"目录名{verb}：改名 {result.Renamed}，跳过 {result.Skipped}，失败 {result.Failures.Count} —— "
              + string.Join("；", result.Failures.Take(3));
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string appExe = Environment.ProcessPath ?? "CarroCasket.exe";
            if (IsContextMenuInstalled)
            {
                ShellContextMenu.Uninstall();
                StatusMessage = "已移除 Windows 右键菜单。";
            }
            else
            {
                ShellContextMenu.Install(appExe);
                StatusMessage = "已添加 Windows 右键菜单。";
            }
            RefreshContextMenuStatus();
        }
        catch (Exception ex) { StatusMessage = $"操作右键菜单失败: {ex.Message}"; }
    }
}
