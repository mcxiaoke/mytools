using System;
using System.Windows;
using System.Windows.Input;
using HideFiles.Core;
using Wpf.Ui.Controls;

namespace HideFiles.App.Views;

/// <summary>
/// 还原/管理操作的安全验证对话框：默认输入 PIN，也可切换为恢复码
/// （恢复码在 PIN 遗忘或密钥材料被清除时是唯一凭据）。
/// </summary>
public partial class PinVerifyDialog : FluentWindow
{
    private readonly HideFilesConfig _config;

    public bool IsVerified { get; private set; }
    /// <summary>验证通过时用户输入的 PIN 明文（v2 解密需要真实 PIN，而非哈希）</summary>
    public string EnteredPin { get; private set; } = string.Empty;
    /// <summary>验证通过时用户输入的恢复码（已规范化：去空格/横线并大写）</summary>
    public string EnteredRecoveryCode { get; private set; } = string.Empty;

    public PinVerifyDialog(HideFilesConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DoVerify();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoVerify();
    }

    private void RecoveryMode_Changed(object sender, RoutedEventArgs e)
    {
        bool recovery = RecoveryMode.IsChecked == true;
        PinBox.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;
        RecoveryBox.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = recovery
            ? "请输入恢复码："
            : "请输入密码：";
        ErrorText.Visibility = Visibility.Collapsed;

        if (recovery) RecoveryBox.Focus();
        else PinBox.Focus();
    }

    private void DoVerify()
    {
        if (RecoveryMode.IsChecked == true)
        {
            DoVerifyRecoveryCode();
            return;
        }

        string input = PinBox.Password;
        if (_config.VerifyPin(input))
        {
            IsVerified = true;
            EnteredPin = input;
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError("密码错误，请重新输入。");
            PinBox.SelectAll();
            PinBox.Focus();
        }
    }

    /// <summary>
    /// 恢复码此处只校验格式与长度（是否为 52 位 Base32）；是否真的属于这批文件，
    /// 由后续解密时的 GCM 认证标签判定——错误恢复码会明确报"校验失败"。
    /// </summary>
    private void DoVerifyRecoveryCode()
    {
        string raw = RecoveryBox.Text;
        try
        {
            V2Crypto.RecoveryCodeToMasterKey(raw);
            IsVerified = true;
            EnteredPin = string.Empty;
            EnteredRecoveryCode = NormalizeRecoveryCode(raw);
            DialogResult = true;
            Close();
        }
        catch (Exception)
        {
            ShowError("恢复码格式错误（需为 52 位字符）。");
            RecoveryBox.Focus();
        }
    }

    private static string NormalizeRecoveryCode(string code) =>
        code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
