using System.Windows;

namespace ScreenLock.Views
{
    public partial class VerifyPinWindow : Window
    {
        private readonly Services.LockController _controller;

        public VerifyPinWindow(Services.LockController controller, string title)
        {
            InitializeComponent();
            _controller = controller;
            TitleText.Text = title;
            Loaded += (s, e) => PinBox.Focus();
            PinBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    OnOkClick(this, null);
                    e.Handled = true;
                }
            };
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (_controller.VerifyForExit(PinBox.Password))
            {
                DialogResult = true;
                Close();
                return;
            }
            MessageText.Text = "PIN 错误。";
            PinBox.Clear();
            PinBox.Focus();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
