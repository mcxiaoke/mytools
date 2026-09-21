using System.Windows;
using System.Windows.Input;

namespace KeepOn.Views
{
    /// <summary>轻量文本输入对话框，用于"自定义分钟数""自定义时刻"等场景。</summary>
    public partial class InputDialog : Window
    {
        public string InputText => InputBox.Text;

        public InputDialog(string title, string prompt, string defaultValue)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(title)) Title = title;
            PromptText.Text = prompt ?? string.Empty;
            InputBox.Text = defaultValue ?? string.Empty;

            Loaded += (s, e) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        }
    }
}
