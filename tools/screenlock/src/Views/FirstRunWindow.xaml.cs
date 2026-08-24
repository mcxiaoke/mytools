using System.Windows;

namespace ScreenLock.Views
{
    public partial class FirstRunWindow : Window
    {
        public string NewPin { get; private set; }

        public FirstRunWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PinBox1.Focus();
            PinBox1.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    PinBox2.Focus();
                    e.Handled = true;
                }
            };
            PinBox2.KeyDown += (s, e) =>
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
            var pin1 = PinBox1.Password;
            var pin2 = PinBox2.Password;
            if (string.IsNullOrEmpty(pin1) || pin1.Length < 4)
            {
                MessageText.Text = "PIN 至少需要 4 位。";
                return;
            }
            if (pin1 != pin2)
            {
                MessageText.Text = "两次输入不一致，请重新输入。";
                return;
            }
            NewPin = pin1;
            DialogResult = true;
            Close();
        }
    }
}
