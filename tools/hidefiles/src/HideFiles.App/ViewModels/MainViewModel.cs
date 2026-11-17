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
    /// <summary>本次会话验证通过的恢复码（与 PIN 二选一，均可用于还原）</summary>
    private string? _sessionRecoveryCode;
    /// <summary>本机 config 中当前 PIN 对应的主密钥（改过 PIN 时兜底，避免每个文件都跑一次 PBKDF2）</summary>
    private byte[]? _sessionMasterKey;

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

    /// <summary>弹出安全验证对话框，记录本会话凭据（PIN 或恢复码）</summary>
    private bool TryPromptCredential()
    {
        var dlg = new Views.PinVerifyDialog(Config) { Owner = Application.Current?.MainWindow };
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
    /// 还原所需凭据：PIN 或恢复码任一即可。
    /// 即使本机已没有密钥材料（PIN 被清除），也允许用恢复码还原。
    /// </summary>
    public bool EnsureRestoreCredential()
    {
        if (!string.IsNullOrEmpty(_sessionPin) || !string.IsNullOrEmpty(_sessionRecoveryCode)) return true;
        return TryPromptCredential();
    }

    /// <summary>管理类操作（清除/修改 PIN）的身份验证：未设置 PIN 时无需验证</summary>
    public bool EnsurePinVerified()
    {
        if (!Config.HasPin) return true;
        if (!string.IsNullOrEmpty(_sessionPin) && Config.VerifyPin(_sessionPin)) return true;
        return TryPromptCredential();
    }

    [RelayCommand]
    private void SavePin()
    {
        if (string.IsNullOrWhiteSpace(NewPinInput)) { StatusMessage = "请输入有效的 PIN 码。"; return; }

        string? currentPin = null;
        if (Config.HasKeyMaterial)
        {
            // 修改已有 PIN：先验证当前 PIN，只重新封装同一主密钥（恢复码不变、历史文件仍可用）
            if (!EnsurePinVerified()) { StatusMessage = "需要验证当前 PIN 才能修改。"; return; }
            currentPin = _sessionPin;
        }

        var result = Config.ChangePin(NewPinInput, currentPin);
        if (!result.Success) { StatusMessage = result.ErrorMessage ?? "设置 PIN 失败。"; return; }

        Config.Save();
        HasPinConfigured = Config.HasPin;
        _sessionPin = Config.SessionPin;
        RefreshSessionMasterKey();
        NewPinInput = string.Empty;
        if (result.MasterKeyRecreated)
        {
            RecoveryCodeDisplay = result.RecoveryCode ?? string.Empty;
            StatusMessage = "安全 PIN 已设置！请妥善保存生成的恢复码（仅显示一次）。";
        }
        else
        {
            StatusMessage = "PIN 已更新：主密钥与恢复码均未变，此前保护的文件仍可用新 PIN 还原。";
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        if (!EnsurePinVerified()) { StatusMessage = "需要验证当前 PIN 码后方可清除。"; return; }

        var confirm = MessageBox.Show(
            "清除 PIN 会删除本机保存的密钥材料，历史文件将只能用恢复码还原。\n\n请确认已离线保存恢复码。继续？",
            "清除 PIN 保护", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        Config.ClearKeyMaterial();
        Config.Save();
        HasPinConfigured = false;
        _sessionPin = null;
        _sessionRecoveryCode = null;
        _sessionMasterKey = null;
        NewPinInput = string.Empty;
        StatusMessage = "PIN 已清除；历史文件仍可用恢复码还原。";
    }

    /// <summary>确保加密所需的 PIN 会话已解锁（恢复码只能用于还原，不能用于加密新文件）</summary>
    private bool EnsureEncryptionReady(string action)
    {
        if (!Config.HasPin || !Config.HasKeyMaterial)
        {
            StatusMessage = $"请先设置 PIN 码（v2 加密需要 PIN 保护密钥），{action}已中止。";
            return false;
        }
        if (string.IsNullOrEmpty(Config.SessionPin) && !EnsurePinVerified())
        {
            StatusMessage = $"需要输入 PIN 解锁加密密钥，{action}已中止。";
            return false;
        }
        if (string.IsNullOrEmpty(Config.SessionPin))
        {
            StatusMessage = $"{action}需要 PIN（恢复码只能用于还原），已中止。";
            return false;
        }
        return true;
    }

    // ---------- 结构锁工作台 ----------
    [RelayCommand]
    private async Task ObfuscateSelectedAsync()
        => await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: true);

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (!EnsureRestoreCredential()) { StatusMessage = "未提供可用的 PIN 或恢复码，还原已中止。"; return; }
        await ProcessSelectedAsync(Files, ProtectionMode.StructureLock, isObfuscate: false);
    }

    // ---------- 整体加密工作台 ----------
    [RelayCommand]
    private async Task EncryptSelectedAsync()
        => await ProcessSelectedAsync(SecureFiles, ProtectionMode.FullEncrypt, isObfuscate: true);

    [RelayCommand]
    private async Task DecryptSelectedAsync()
    {
        if (!EnsureRestoreCredential()) { StatusMessage = "未提供可用的 PIN 或恢复码，解密已中止。"; return; }
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
        if (!isObfuscate && !EnsureRestoreCredential()) return;
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
                    : _obfuscator.RestoreFile(item.FilePath, _sessionPin, _sessionRecoveryCode, _sessionMasterKey);

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
                string? currentPin = null;
                if (Config.HasKeyMaterial)
                {
                    if (!EnsurePinVerified()) { StatusMessage = "需要验证当前 PIN 才能修改，配置未保存。"; return; }
                    currentPin = _sessionPin;
                }

                var result = Config.ChangePin(NewPinInput, currentPin);
                if (!result.Success) { StatusMessage = result.ErrorMessage ?? "设置 PIN 失败。"; return; }

                HasPinConfigured = Config.HasPin;
                _sessionPin = Config.SessionPin;
                RefreshSessionMasterKey();
                NewPinInput = string.Empty;
                if (result.MasterKeyRecreated) RecoveryCodeDisplay = result.RecoveryCode ?? string.Empty;
            }
            Config.Save();
            StatusMessage = "所有配置变更已成功保存！";
        }
        catch (Exception ex) { StatusMessage = $"保存配置失败: {ex.Message}"; }
    }

    [RelayCommand]
    private void ResetConfig()
    {
        // 只重置规则，保留密钥材料——"重置为默认配置"不应顺手销毁解密历史文件的能力
        var fresh = HideFilesConfig.CreateDefault();
        fresh.PinHash = Config.PinHash;
        fresh.ConfigSalt = Config.ConfigSalt;
        fresh.WrappedMasterKey = Config.WrappedMasterKey;
        fresh.RecoveryCodeHash = Config.RecoveryCodeHash;
        fresh.KdfIterations = Config.KdfIterations;
        fresh.SessionPin = Config.SessionPin;

        Config = fresh;
        Config.Save();
        HasPinConfigured = Config.HasPin;
        RefreshSessionMasterKey();
        StatusMessage = "已恢复默认规则（PIN 与密钥材料保持不变）。";
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
