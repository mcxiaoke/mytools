using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HideFiles.App.Models;
using HideFiles.Core;
using Microsoft.Win32;

namespace HideFiles.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileObfuscator _obfuscator = new();
    /// <summary>本次会话验证通过的 PIN（v2 解密用明文 PIN，不落盘）</summary>
    private string? _sessionPin;

    [ObservableProperty]
    private string _title = "HideFiles - 极速文件自包含混淆保护工具";

    [ObservableProperty]
    private HideFilesConfig _config;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "就绪。拖拽文件或文件夹到窗口开始处理。";

    [ObservableProperty]
    private bool _isContextMenuInstalled;

    [ObservableProperty]
    private string _newPinInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryCode))]
    private string _recoveryCodeDisplay = string.Empty;

    public bool HasRecoveryCode => !string.IsNullOrEmpty(RecoveryCodeDisplay);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfiguredPinDisplay))]
    private bool _hasPinConfigured;

    public string ConfiguredPinDisplay => HasPinConfigured ? "已启用保护 (●●●●●●)" : "未设置";

    /// <summary>当前活动工作台：struct=结构锁 secure=整体加密（由 Tab 切换设置，决定拖拽目标）</summary>
    [ObservableProperty]
    private string _activeMode = "struct";

    /// <summary>结构锁工作台文件列表</summary>
    public ObservableCollection<FileItemModel> Files { get; } = new();
    /// <summary>整体加密工作台文件列表</summary>
    public ObservableCollection<FileItemModel> SecureFiles { get; } = new();

    public int TotalCount => Files.Count;
    public int NormalCount => Files.Count(f => f.Status == FileStatus.Normal);
    public int ObfuscatedCount => Files.Count(f => f.Status == FileStatus.Obfuscated);
    public int SecureTotalCount => SecureFiles.Count;
    public int SecureNormalCount => SecureFiles.Count(f => f.Status == FileStatus.Normal);
    public int SecureObfuscatedCount => SecureFiles.Count(f => f.Status == FileStatus.Obfuscated);

    public MainViewModel()
    {
        _config = HideFilesConfig.Load();
        HasPinConfigured = _config.HasPin;
        RefreshContextMenuStatus();
    }

    private ObservableCollection<FileItemModel> CollectionFor(string mode)
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
        OnPropertyChanged(nameof(SecureTotalCount));
        OnPropertyChanged(nameof(SecureNormalCount));
        OnPropertyChanged(nameof(SecureObfuscatedCount));
    }

    /// <summary>添加路径到指定工作台（mode: struct/secure）</summary>
    public void AddPaths(IEnumerable<string> paths, string? mode = null)
    {
        var collection = CollectionFor(mode ?? ActiveMode);
        var existing = new HashSet<string>(collection.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (File.Exists(p))
                AddSingleFile(p, existing, collection);
            else if (Directory.Exists(p))
            {
                var opt = Config.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(p, "*.*", opt))
                    AddSingleFile(f, existing, collection);
            }
        }

        UpdateCounts();
        StatusMessage = $"已加载 {collection.Count} 个文件 (普通: {collection.Count(f => f.Status == FileStatus.Normal)}, 已保护: {collection.Count(f => f.Status == FileStatus.Obfuscated)})。";
    }

    private void AddSingleFile(string file, HashSet<string> existing, ObservableCollection<FileItemModel> collection)
    {
        string full = Path.GetFullPath(file);
        if (existing.Contains(full)) return;

        var insp = _obfuscator.Inspect(full);
        var item = new FileItemModel
        {
            FilePath = full,
            FileName = Path.GetFileName(full),
            FileSize = insp.FileSize,
            Status = insp.Status,
            OriginalFileName = insp.OriginalFileName,
            StatusText = insp.Status switch
            {
                FileStatus.Obfuscated => insp.ProtectionMode == ProtectionMode.FullEncrypt ? "已加密" : "已混淆",
                FileStatus.Normal => "普通",
                FileStatus.Corrupted => "损坏",
                FileStatus.Locked => "已占用",
                _ => "未知"
            }
        };
        collection.Add(item);
        existing.Add(full);
    }

    [RelayCommand]
    private void SelectFiles(string mode)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要处理的文件" };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames, mode);
    }

    [RelayCommand]
    private void SelectFolder(string mode)
    {
        var dlg = new OpenFolderDialog { Title = "选择包含目标文件的文件夹", Multiselect = false };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            AddPaths(new[] { dlg.FolderName }, mode);
    }

    [RelayCommand]
    private void ClearFiles(string mode)
    {
        CollectionFor(mode).Clear();
        UpdateCounts();
        ProgressValue = 0;
        StatusMessage = "列表已清空。";
    }

    public bool EnsurePinVerified()
    {
        if (!Config.HasPin) return true;
        var dlg = new Views.PinVerifyDialog(Config) { Owner = Application.Current?.MainWindow };
        bool? result = dlg.ShowDialog();
        if (result == true && dlg.IsVerified)
        {
            _sessionPin = dlg.EnteredPin;
            Config.SessionPin = _sessionPin;
            return true;
        }
        return false;
    }

    [RelayCommand]
    private void SavePin()
    {
        if (string.IsNullOrWhiteSpace(NewPinInput)) { StatusMessage = "请输入有效的 PIN 码。"; return; }
        string recovery = Config.SetPinV2(NewPinInput);
        Config.Save();
        HasPinConfigured = Config.HasPin;
        _sessionPin = Config.SessionPin;
        NewPinInput = string.Empty;
        RecoveryCodeDisplay = recovery;
        StatusMessage = "安全 PIN 码已设置！请妥善保存生成的恢复码（仅显示一次）。";
    }

    [RelayCommand]
    private void ClearPin()
    {
        if (!EnsurePinVerified()) { StatusMessage = "需要验证当前 PIN 码后方可清除。"; return; }
        Config.SetPin(null);
        Config.Save();
        HasPinConfigured = false;
        NewPinInput = string.Empty;
        StatusMessage = "PIN 码保护已清除。";
    }

    /// <summary>确保加密所需的 PIN 会话已解锁</summary>
    private bool EnsureEncryptionReady(string action)
    {
        if (!Config.HasPin) { StatusMessage = $"请先设置 PIN 码（v2 加密需要 PIN 保护密钥），{action}已中止。"; return false; }
        if (string.IsNullOrEmpty(Config.SessionPin) && !EnsurePinVerified())
        { StatusMessage = $"需要输入 PIN 解锁加密密钥，{action}已中止。"; return false; }
        return true;
    }

    // ---------- 结构锁工作台 ----------
    [RelayCommand]
    private async Task ObfuscateSelectedAsync()
        => await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: true);

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (!EnsurePinVerified()) { StatusMessage = "PIN 验证未通过或已取消，还原已中止。"; return; }
        await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: false);
    }

    // ---------- 整体加密工作台 ----------
    [RelayCommand]
    private async Task EncryptSelectedAsync()
        => await ProcessSelectedAsync(SecureFiles, ProtectionMode.FullEncrypt, isObfuscate: true);

    [RelayCommand]
    private async Task DecryptSelectedAsync()
    {
        if (!EnsurePinVerified()) { StatusMessage = "PIN 验证未通过或已取消，解密已中止。"; return; }
        await ProcessSelectedAsync(SecureFiles, ProtectionMode.FullEncrypt, isObfuscate: false);
    }

    private async Task ProcessSelectedAsync(ObservableCollection<FileItemModel> collection,
        ProtectionMode mode, bool isObfuscate)
    {
        var targets = collection.Where(f => f.IsSelected &&
            (isObfuscate ? f.Status == FileStatus.Normal : f.Status == FileStatus.Obfuscated)).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = isObfuscate ? "未选择可处理的普通文件。" : "未选择已保护的文件。";
            return;
        }
        if (isObfuscate && !EnsureEncryptionReady(mode == ProtectionMode.FullEncrypt ? "加密" : "混淆")) return;

        var prevMode = Config.ProtectionMode;
        Config.ProtectionMode = mode;
        await ProcessAsync(targets, isObfuscate);
        Config.ProtectionMode = prevMode;
    }

    /// <summary>供右键菜单/启动参数调用：自动处理路径（按模式选集合与操作）</summary>
    public async Task AutoProcessPathsAsync(IEnumerable<string> paths, ProtectionMode mode, bool isObfuscate)
    {
        string m = mode == ProtectionMode.FullEncrypt ? "secure" : "struct";
        AddPaths(paths, m);
        var collection = CollectionFor(m);
        if (isObfuscate && !EnsureEncryptionReady(mode == ProtectionMode.FullEncrypt ? "加密" : "混淆")) return;
        if (!isObfuscate && !EnsurePinVerified()) return;
        await ProcessAsync(collection.Where(f =>
            (isObfuscate ? f.Status == FileStatus.Normal : f.Status == FileStatus.Obfuscated)).ToList(), isObfuscate, mode);
    }

    private async Task ProcessAsync(List<FileItemModel> targetItems, bool isObfuscate,
        ProtectionMode? modeOverride = null)
    {
        if (modeOverride.HasValue) Config.ProtectionMode = modeOverride.Value;
        IsProcessing = true;
        ProgressValue = 0;
        int total = targetItems.Count;
        int successCount = 0;

        await Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                var item = targetItems[i];
                StatusMessage = $"正在处理 ({i + 1}/{total}): {item.FileName}";
                ProcessFileResult res = isObfuscate
                    ? _obfuscator.ObfuscateFile(item.FilePath, Config)
                    : _obfuscator.RestoreFile(item.FilePath, _sessionPin);

                if (res.Success)
                {
                    successCount++;
                    var insp = _obfuscator.Inspect(res.FinalPath);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.FilePath = res.FinalPath;
                        item.FileName = Path.GetFileName(res.FinalPath);
                        item.Status = insp.Status;
                        item.OriginalFileName = insp.OriginalFileName;
                        item.StatusText = isObfuscate
                            ? (Config.ProtectionMode == ProtectionMode.FullEncrypt ? "已加密" : "已混淆")
                            : "普通";
                    });
                }
                double pct = (double)(i + 1) / total * 100.0;
                Application.Current.Dispatcher.Invoke(() => ProgressValue = pct);
            }
        });

        IsProcessing = false;
        UpdateCounts();
        StatusMessage = $"处理完成！成功: {successCount}/{total}。";
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(NewPinInput))
            {
                string recovery = Config.SetPinV2(NewPinInput);
                HasPinConfigured = Config.HasPin;
                _sessionPin = Config.SessionPin;
                NewPinInput = string.Empty;
                RecoveryCodeDisplay = recovery;
            }
            Config.Save();
            StatusMessage = "所有配置变更已成功保存！";
        }
        catch (Exception ex) { StatusMessage = $"保存配置失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void ResetConfig()
    {
        Config = HideFilesConfig.CreateDefault();
        Config.Save();
        HasPinConfigured = Config.HasPin;
        StatusMessage = "已恢复为默认配置。";
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string appExe = Environment.ProcessPath ?? "HideFilesApp.exe";
            string dir = Path.GetDirectoryName(appExe)!;
            string cliExe = Path.Combine(dir, "hidefiles.exe");
            if (!File.Exists(cliExe)) cliExe = appExe;
            if (IsContextMenuInstalled)
            {
                ShellContextMenu.Uninstall();
                StatusMessage = "已成功移除 Windows 右键菜单。";
            }
            else
            {
                ShellContextMenu.Install(appExe, cliExe);
                StatusMessage = "已成功安装 Windows 四动作右键菜单！";
            }
            RefreshContextMenuStatus();
        }
        catch (Exception ex) { StatusMessage = $"操作右键菜单失败: {ex.Message}"; }
    }
}
