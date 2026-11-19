using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace HideFiles.App.Views;

public partial class ScanProgressDialog : FluentWindow
{
    private readonly System.Threading.CancellationTokenSource _cts;
    private bool _isClosing;

    public ScanProgressDialog(System.Threading.CancellationTokenSource cts)
    {
        InitializeComponent();
        _cts = cts;
    }

    public void ReportSearch(int count, string currentPath)
    {
        if (_isClosing) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing) return;
            StatusText.Text = "正在搜索目录与文件...";
            CountText.Text = $"已发现 {count:N0} 个";
            ProgressBarControl.IsIndeterminate = true;
            CurrentPathText.Text = currentPath;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ReportInspect(int current, int total, string currentFile)
    {
        if (_isClosing) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing) return;
            StatusText.Text = $"正在分析文件属性 ({current:N0} / {total:N0})...";
            int pct = total > 0 ? (int)((double)current / total * 100) : 0;
            CountText.Text = $"{pct}%";
            ProgressBarControl.IsIndeterminate = false;
            ProgressBarControl.Maximum = total;
            ProgressBarControl.Value = current;
            CurrentPathText.Text = currentFile;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void CloseDialog()
    {
        if (_isClosing) return;
        _isClosing = true;
        Dispatcher.Invoke(() =>
        {
            try
            {
                Close();
            }
            catch
            {
                // 忽略已关闭异常
            }
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消...";
        _cts.Cancel();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        base.OnClosing(e);
    }
}
