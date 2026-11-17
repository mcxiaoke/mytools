using System.Windows;
using System.Windows.Input;
using HideFiles.Core;
using Wpf.Ui.Controls;

namespace HideFiles.App.Views;

public partial class PinVerifyDialog : FluentWindow
{
    private readonly HideFilesConfig _config;

    public bool IsVerified { get; private set; }

    public PinVerifyDialog(HideFilesConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DoVerify();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoVerify();
        }
    }

    private void DoVerify()
    {
        string input = PinBox.Password;
        if (_config.VerifyPin(input))
        {
            IsVerified = true;
            DialogResult = true;
            Close();
        }
        else
        {
            ErrorText.Text = "PIN 码不正确，请重新输入。";
            ErrorText.Visibility = Visibility.Visible;
            PinBox.SelectAll();
            PinBox.Focus();
        }
    }
}
