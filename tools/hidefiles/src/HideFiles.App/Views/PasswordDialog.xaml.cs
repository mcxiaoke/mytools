using System;
using System.Windows;
using System.Windows.Input;
using HideFiles.Core;
using Wpf.Ui.Controls;

namespace HideFiles.App.Views;

public enum PasswordDialogMode
{
    Set,
    Change,
    Verify
}

public partial class PasswordDialog : FluentWindow
{
    private readonly HideFilesConfig _config;
    private readonly PasswordDialogMode _mode;
    private readonly bool _allowRecovery;

    public bool IsVerified { get; private set; }
    public string CurrentPin { get; private set; } = string.Empty;
    public string EnteredPin { get; private set; } = string.Empty;
    public string EnteredRecoveryCode { get; private set; } = string.Empty;

    public PasswordDialog(HideFilesConfig config, PasswordDialogMode mode = PasswordDialogMode.Verify, bool allowRecovery = true)
    {
        InitializeComponent();
        _config = config;
        _mode = mode;
        _allowRecovery = allowRecovery;
        ConfigureMode();
        Loaded += (_, _) => FocusFirstInput();
    }

    private void ConfigureMode()
    {
        switch (_mode)
        {
            case PasswordDialogMode.Set:
                Title = "设置主密码";
                TitleBarControl.Title = "设置主密码";
                Height = 360;
                HintText.Text = "首次设置主密码，请确认两次输入一致：";
                PasswordLabel.Text = "新密码：";
                CurrentPasswordPanel.Visibility = Visibility.Collapsed;
                ConfirmPasswordPanel.Visibility = Visibility.Visible;
                RecoveryModeCheck.Visibility = Visibility.Collapsed;
                RecoveryPanel.Visibility = Visibility.Collapsed;
                break;

            case PasswordDialogMode.Change:
                Title = "修改主密码";
                TitleBarControl.Title = "修改主密码";
                Height = 440;
                HintText.Text = "修改主密码需要验证当前密码，并输入两遍新密码：";
                PasswordLabel.Text = "新密码：";
                CurrentPasswordPanel.Visibility = Visibility.Visible;
                ConfirmPasswordPanel.Visibility = Visibility.Visible;
                RecoveryModeCheck.Visibility = Visibility.Collapsed;
                RecoveryPanel.Visibility = Visibility.Collapsed;
                break;

            case PasswordDialogMode.Verify:
            default:
                Title = _allowRecovery ? "解密验证" : "输入密码";
                TitleBarControl.Title = Title;
                Height = _allowRecovery ? 330 : 280;
                HintText.Text = _allowRecovery
                    ? "请输入密码进行解密验证（或使用恢复码）："
                    : "加密文件需要使用主密码，请输入密码：";
                PasswordLabel.Text = "密码：";
                CurrentPasswordPanel.Visibility = Visibility.Collapsed;
                ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
                RecoveryModeCheck.Visibility = _allowRecovery ? Visibility.Visible : Visibility.Collapsed;
                RecoveryPanel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void FocusFirstInput()
    {
        if (_mode == PasswordDialogMode.Change)
            CurrentPasswordBox.Focus();
        else
            PasswordBoxControl.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DoValidateAndConfirm();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoValidateAndConfirm();
    }

    private void RecoveryMode_Changed(object sender, RoutedEventArgs e)
    {
        bool recovery = RecoveryModeCheck.IsChecked == true;
        PasswordPanel.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;
        RecoveryPanel.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = recovery ? "请输入 52 位紧急恢复码：" : "请输入密码进行身份验证：";
        ErrorText.Visibility = Visibility.Collapsed;

        if (recovery) RecoveryBox.Focus();
        else PasswordBoxControl.Focus();
    }

    private void DoValidateAndConfirm()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (_mode == PasswordDialogMode.Verify)
        {
            if (_allowRecovery && RecoveryModeCheck.IsChecked == true)
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
                catch
                {
                    ShowError("恢复码格式错误（需为 52 位字符）。");
                    RecoveryBox.Focus();
                }
                return;
            }

            string input = PasswordBoxControl.Password;
            if (string.IsNullOrEmpty(input))
            {
                ShowError("请输入密码。");
                PasswordBoxControl.Focus();
                return;
            }

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
                PasswordBoxControl.SelectAll();
                PasswordBoxControl.Focus();
            }
            return;
        }

        if (_mode == PasswordDialogMode.Change)
        {
            string current = CurrentPasswordBox.Password;
            if (string.IsNullOrEmpty(current))
            {
                ShowError("请输入当前密码。");
                CurrentPasswordBox.Focus();
                return;
            }
            if (!_config.VerifyPin(current))
            {
                ShowError("当前密码错误，请重新输入。");
                CurrentPasswordBox.SelectAll();
                CurrentPasswordBox.Focus();
                return;
            }
            CurrentPin = current;
        }

        string newPin = PasswordBoxControl.Password;
        string confirmPin = ConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(newPin))
        {
            ShowError("新密码不能为空。");
            PasswordBoxControl.Focus();
            return;
        }

        if (newPin != confirmPin)
        {
            ShowError("两次输入的密码不一致，请仔细核对。");
            ConfirmPasswordBox.SelectAll();
            ConfirmPasswordBox.Focus();
            return;
        }

        IsVerified = true;
        EnteredPin = newPin;
        DialogResult = true;
        Close();
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string NormalizeRecoveryCode(string code) =>
        code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
}
