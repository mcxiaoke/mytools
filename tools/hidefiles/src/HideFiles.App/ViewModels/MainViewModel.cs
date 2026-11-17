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

    public ObservableCollection<FileItemModel> Files { get; } = new();

    public int TotalCount => Files.Count;
    public int NormalCount => Files.Count(f => f.Status == FileStatus.Normal);
    public int ObfuscatedCount => Files.Count(f => f.Status == FileStatus.Obfuscated);

    public MainViewModel()
    {
        _config = HideFilesConfig.Load();
        RefreshContextMenuStatus();
    }

    public void RefreshContextMenuStatus()
    {
        if (OperatingSystem.IsWindows())
        {
            IsContextMenuInstalled = ShellContextMenu.IsInstalled();
        }
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(NormalCount));
        OnPropertyChanged(nameof(ObfuscatedCount));
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                AddSingleFile(p, existing);
            }
            else if (Directory.Exists(p))
            {
                var opt = Config.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(p, "*.*", opt))
                {
                    AddSingleFile(f, existing);
                }
            }
        }

        UpdateCounts();
        StatusMessage = $"已加载 {Files.Count} 个文件 (未混淆: {NormalCount}, 已混淆: {ObfuscatedCount})。";
    }

    private void AddSingleFile(string file, HashSet<string> existing)
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
                FileStatus.Obfuscated => "已混淆",
                FileStatus.Normal => "普通",
                FileStatus.Corrupted => "损坏",
                FileStatus.Locked => "已占用",
                _ => "未知"
            }
        };

        Files.Add(item);
        existing.Add(full);
    }

    [RelayCommand]
    private void SelectFiles()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要混淆或还原的文件"
        };

        if (dlg.ShowDialog() == true)
        {
            AddPaths(dlg.FileNames);
        }
    }

    [RelayCommand]
    private void SelectFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择包含目标文件的文件夹",
            Multiselect = false
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            AddPaths(new[] { dlg.FolderName });
        }
    }

    [RelayCommand]
    private void ClearFiles()
    {
        Files.Clear();
        UpdateCounts();
        ProgressValue = 0;
        StatusMessage = "就绪。拖拽文件或文件夹到窗口开始处理。";
    }

    [RelayCommand]
    private async Task ObfuscateSelectedAsync()
    {
        var targets = Files.Where(f => f.IsSelected && f.Status == FileStatus.Normal).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "未选择可混淆的普通文件。";
            return;
        }

        await ProcessAsync(targets, isObfuscate: true);
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        var targets = Files.Where(f => f.IsSelected && f.Status == FileStatus.Obfuscated).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "未选择已混淆的文件。";
            return;
        }

        await ProcessAsync(targets, isObfuscate: false);
    }

    private async Task ProcessAsync(List<FileItemModel> targetItems, bool isObfuscate)
    {
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
                    : _obfuscator.RestoreFile(item.FilePath);

                if (res.Success)
                {
                    successCount++;
                    // 更新 item 状态
                    var insp = _obfuscator.Inspect(res.FinalPath);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.FilePath = res.FinalPath;
                        item.FileName = Path.GetFileName(res.FinalPath);
                        item.Status = insp.Status;
                        item.OriginalFileName = insp.OriginalFileName;
                        item.StatusText = isObfuscate ? "已混淆" : "普通";
                    });
                }

                double pct = (double)(i + 1) / total * 100.0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressValue = pct;
                });
            }
        });

        IsProcessing = false;
        UpdateCounts();
        StatusMessage = $"处理完成！成功: {successCount}/{total}。";

        if (OperatingSystem.IsWindows())
        {
            ShellContextMenu.RefreshShell();
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            Config.Save();
            StatusMessage = "配置已保存。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存配置失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetConfig()
    {
        Config = HideFilesConfig.CreateDefault();
        Config.Save();
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
                StatusMessage = "已成功安装 Windows 级联折叠右键菜单！";
            }
            RefreshContextMenuStatus();
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作右键菜单失败: {ex.Message}";
        }
    }
}
