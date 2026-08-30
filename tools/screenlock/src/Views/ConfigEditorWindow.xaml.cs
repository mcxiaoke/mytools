using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScreenLock.Models;
using ScreenLock.Services;

namespace ScreenLock.Views
{
    public partial class ConfigEditorWindow : Window
    {
        private AppSettings _editing;

        public ConfigEditorWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            try
            {
                var cur = App.Config.Current;
                _editing = cur.Clone();

                PathText.Text = ConfigService.FilePath;
                ModeText.Text = ConfigService.IsPortableMode ? "便携模式（exe 同目录）" : "漫游模式（%AppData%\\ScreenLock）";

                // IdleMinutes -> ComboBox (editable)
                string idleStr = _editing.IdleMinutes.ToString();
                bool found = false;
                for (int i = 0; i < IdleBox.Items.Count; i++)
                {
                    var item = IdleBox.Items[i] as ComboBoxItem;
                    if (item != null)
                    {
                        string txt = item.Content.ToString();
                        // "0 - 禁用" or "5"
                        string num = txt.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)[0];
                        if (num == idleStr) { IdleBox.SelectedIndex = i; found = true; break; }
                    }
                }
                if (!found) { IdleBox.Text = idleStr; IdleBox.SelectedIndex = -1; }
                else IdleBox.Text = idleStr;

                ShowClockBox.IsChecked = _editing.ShowClock;
                OpacitySlider.Value = _editing.OverlayOpacity;
                OpacityText.Text = _editing.OverlayOpacity.ToString("0.00");
                AutoStartBox.IsChecked = _editing.AutoStart;
                TasksEnabledBox.IsChecked = _editing.TasksEnabled;

                ExcludeList.ItemsSource = null;
                var list = _editing.ExcludeProcesses != null ? new List<string>(_editing.ExcludeProcesses) : new List<string>();
                ExcludeList.ItemsSource = list;

                PinStatusText.Text = _editing.HasPin() ? "已设置（" + MaskHash(_editing.PinHash) + "）" : "未设置";
                ValidateText.Text = "";
                ExcludeInputBox.KeyDown += ExcludeInputBox_KeyDown;
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载配置失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string MaskHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            if (hash.Length <= 8) return "***";
            return hash.Substring(0, 6) + "***";
        }

        private void ExcludeInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { OnExcludeAddClick(sender, null); e.Handled = true; }
        }

        private void OnExcludeAddClick(object sender, RoutedEventArgs e)
        {
            string v = ExcludeInputBox.Text.Trim();
            if (string.IsNullOrEmpty(v)) return;
            // allow comma separated
            var parts = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            foreach (var p in parts)
            {
                string t = p.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                // dedup case-insensitive
                if (list.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(t);
            }
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = list;
            ExcludeInputBox.Text = "";
        }

        private void OnExcludeDeleteClick(object sender, RoutedEventArgs e)
        {
            var sel = ExcludeList.SelectedItem as string;
            if (sel == null) return;
            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            list.Remove(sel);
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = list;
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityText != null) OpacityText.Text = e.NewValue.ToString("0.00");
        }

        private AppSettings BuildCurrent()
        {
            var s = new AppSettings();
            // IdleMinutes
            string idleRaw = IdleBox.Text.Trim();
            // handle "0 - 禁用" selected
            if (idleRaw.Contains("-")) idleRaw = idleRaw.Split('-')[0].Trim();
            int idle;
            if (!int.TryParse(idleRaw, out idle)) idle = _editing.IdleMinutes;
            s.IdleMinutes = idle;
            s.ShowClock = ShowClockBox.IsChecked == true;
            s.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
            s.AutoStart = AutoStartBox.IsChecked == true;
            s.TasksEnabled = TasksEnabledBox.IsChecked == true;
            var excl = ExcludeList.ItemsSource as List<string>;
            s.ExcludeProcesses = excl != null ? new List<string>(excl) : new List<string>();
            // keep pin
            s.PinSalt = _editing.PinSalt;
            s.PinHash = _editing.PinHash;
            return s;
        }

        private string Validate(AppSettings s)
        {
            if (s.IdleMinutes < 0 || s.IdleMinutes > 24 * 60) return "空闲分钟需在 0-1440 之间";
            if (s.OverlayOpacity < 0.3 || s.OverlayOpacity > 1.0) return "透明度需在 0.3-1.0 之间";
            foreach (var p in s.ExcludeProcesses)
            {
                if (p.Length > 260) return "排除进程名过长: " + p;
                if (p.IndexOfAny(new[] { '<', '>', ':', '\"', '|', '?', '*' }) >= 0) return "排除进程名含非法字符: " + p;
            }
            return null;
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var cur = BuildCurrent();
            string err = Validate(cur);
            if (err != null)
            {
                ValidateText.Text = "校验失败: " + err;
                MessageBox.Show(err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ValidateText.Text = "校验通过";
                MessageBox.Show("校验通过", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(false)) return;
            MessageBox.Show("已保存到 " + ConfigService.FilePath, "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(true)) return;
            MessageBox.Show("已保存并应用", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool DoSave(bool apply)
        {
            var cur = BuildCurrent();
            string err = Validate(cur);
            if (err != null)
            {
                ValidateText.Text = "校验失败: " + err;
                MessageBox.Show(err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                // apply to global config
                cur.CopyTo(App.Config.Current);
                // also update editing copy (for pin)
                _editing = App.Config.Current.Clone();
                App.Config.Save();
                ValidateText.Text = "已保存";

                if (apply)
                {
                    ApplyRuntime(cur);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ApplyRuntime(AppSettings s)
        {
            try
            {
                // pin -> controller
                try { if (App.Controller != null) App.Controller.ApplyPinFromConfig(); } catch { }
                // Idle threshold
                if (App.Idle != null)
                {
                    App.Idle.Threshold = TimeSpan.FromMinutes(s.IdleMinutes);
                    App.Idle.Reset();
                }
                // AutoStart
                AutoStartService.Sync(s.AutoStart);
                // Tasks global switch
                if (App.TaskScheduler != null)
                {
                    App.TaskScheduler.SetGlobalEnabled(s.TasksEnabled);
                }
                // Process exclusion cache
                try { ProcessExclusionService.InvalidateCache(); } catch { }
                // Update tray text/menu if available
                var app = Application.Current as App;
                if (app != null)
                {
                    try { app.Dispatcher.Invoke(new Action(() => { try { typeof(App).GetMethod("RefreshMenuChecks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(app, null); } catch { } })); } catch { }
                    try { app.Dispatcher.Invoke(new Action(() => { try { typeof(App).GetMethod("UpdateTrayText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(app, null); } catch { } })); } catch { }
                }
            }
            catch { }
        }

        private void OnPinChangeClick(object sender, RoutedEventArgs e)
        {
            // verify old pin first if exists
            if (_editing.HasPin())
            {
                var verify = new VerifyPinWindow(App.Controller, "修改 PIN 需先验证原 PIN");
                verify.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                verify.Owner = this;
                if (verify.ShowDialog() != true)
                    return;
            }
            var first = new FirstRunWindow();
            first.Title = "设置新 PIN";
            first.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            first.Owner = this;
            if (first.ShowDialog() == true)
            {
                string newPin = first.NewPin;
                // generate new salt/hash without mutating controller yet (apply on Save)
                byte[] salt = PinService.GenerateSalt();
                string saltStr = Convert.ToBase64String(salt);
                string hashStr = PinService.ComputeHash(salt, newPin);
                _editing.PinSalt = saltStr;
                _editing.PinHash = hashStr;
                PinStatusText.Text = "已设置（" + MaskHash(_editing.PinHash) + "）*未保存";
                ValidateText.Text = "PIN 已修改，请保存";
                MessageBox.Show("新 PIN 已生成，点 保存 后生效", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}
