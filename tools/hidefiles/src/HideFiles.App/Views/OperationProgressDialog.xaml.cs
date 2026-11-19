using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace HideFiles.App.Views;

public partial class OperationProgressDialog : FluentWindow
{
    private readonly System.Threading.CancellationTokenSource _cts;
    private bool _isClosing;

    public OperationProgressDialog(string actionTitle, System.Threading.CancellationTokenSource cts)
    {
        InitializeComponent();
        Title = actionTitle;
        TitleBarControl.Title = actionTitle;
        ActionText.Text = actionTitle;
        _cts = cts;
    }

    public void ReportProgress(int current, int total, int success, int failed, string currentFile)
    {
        if (_isClosing) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing) return;
            double pct = total > 0 ? (double)current / total * 100.0 : 0;
            ProgressBarControl.Value = pct;
            PercentText.Text = $"{(int)pct}%";
            StatsText.Text = failed > 0
                ? $"完成: {current}/{total} (失败: {failed})"
                : $"完成: {current}/{total}";
            CurrentFileText.Text = currentFile;
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
