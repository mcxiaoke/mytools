using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScreenLock.Models;
using ScreenLock.Services.Tasks;

namespace ScreenLock.Views
{
    public partial class TaskEditorWindow : Window
    {
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        private bool _isUpdating = false;

        public TaskEditorWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadTasks();
            RefreshScriptQuick();
            if (_tasks.Count > 0) TaskList.SelectedIndex = 0;
        }

        private void LoadTasks()
        {
            try
            {
                var res = TaskConfigService.Load();
                _tasks = res.Tasks ?? new List<TaskDefinition>();
                if (_tasks.Count == 0 && res.Errors.Count > 0)
                {
                    MessageBox.Show("加载 tasks.json 有错误:\n" + string.Join("\n", res.Errors), "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载失败: " + ex.Message);
                _tasks = new List<TaskDefinition>();
                RefreshList();
            }
        }

        private void RefreshList()
        {
            var sel = TaskList.SelectedIndex;
            TaskList.ItemsSource = null;
            TaskList.ItemsSource = _tasks;
            if (sel >= 0 && sel < _tasks.Count) TaskList.SelectedIndex = sel;
            else if (_tasks.Count > 0) TaskList.SelectedIndex = 0;
            else ClearForm();
        }

        private void RefreshScriptQuick()
        {
            try
            {
                ScriptQuickBox.Items.Clear();
                var hdr = new ComboBoxItem { Content = "scripts/ 快速选择", IsEnabled = false };
                ScriptQuickBox.Items.Add(hdr);
                var dir = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                    foreach (var f in files.OrderBy(x => x))
                    {
                        var name = System.IO.Path.GetFileName(f);
                        var item = new ComboBoxItem { Content = name, Tag = f };
                        ScriptQuickBox.Items.Add(item);
                    }
                    var subs = Directory.GetDirectories(dir);
                    foreach (var sub in subs)
                    {
                        var files2 = Directory.GetFiles(sub, "*.*", SearchOption.TopDirectoryOnly);
                        foreach (var f in files2.OrderBy(x => x))
                        {
                            var rel = f.Substring(dir.Length).TrimStart('\\', '/');
                            var item = new ComboBoxItem { Content = rel, Tag = f };
                            ScriptQuickBox.Items.Add(item);
                        }
                    }
                }
                ScriptQuickBox.SelectedIndex = 0;
                ScriptQuickBox.SelectionChanged += ScriptQuickBox_SelectionChanged;
            }
            catch { }
        }

        private void ScriptQuickBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating) return;
            var item = ScriptQuickBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null) return;
            string full = item.Tag.ToString();
            string rel = item.Content.ToString();
            // if scripts dir, we can use bare name
            FileBox.Text = rel;
            // auto fill workdir if empty
            if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) WorkDirBox.Text = dir;
                }
                catch { }
            }
            ScriptQuickBox.SelectedIndex = 0;
        }

        private void ClearForm()
        {
            _isUpdating = true;
            NameBox.Text = "";
            EnabledBox.IsChecked = true;
            TriggerTypeBox.SelectedIndex = -1;
            DelayBox.Text = "5";
            EveryBox.Text = "";
            AtBox.Text = "";
            CronBox.Text = "";
            IdleBox.Text = "10";
            HotkeyBox.Text = "";
            WatchPathBox.Text = "";
            WatchFilterBox.Text = "*.*";
            WatchEventBox.SelectedIndex = 0;
            FileBox.Text = "";
            ArgsBox.Text = "";
            WorkDirBox.Text = "";
            HiddenBox.IsChecked = true;
            AllowConcurrentBox.IsChecked = false;
            NotifyBox.IsChecked = true;
            TimeoutBox.Text = "0";
            RetryBox.Text = "0";
            OnlyIdleBox.IsChecked = false;
            AcPowerBox.IsChecked = false;
            NetworkBox.IsChecked = false;
            FileExistsBox.Text = "";
            FileNotExistsBox.Text = "";
            ValidateText.Text = "";
            UpdateTriggerPanels();
            _isUpdating = false;
        }

        private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating) return;
            var task = TaskList.SelectedItem as TaskDefinition;
            if (task == null) { ClearForm(); return; }
            _isUpdating = true;
            NameBox.Text = task.Name;
            EnabledBox.IsChecked = task.Enabled;
            // trigger type
            string tag = task.Trigger.RawType != "" ? task.Trigger.RawType : task.Trigger.Type.ToString().ToLowerInvariant();
            SelectTriggerTag(tag);
            DelayBox.Text = task.Trigger.DelaySec.ToString();
            EveryBox.Text = !string.IsNullOrWhiteSpace(task.Trigger.Every) ? task.Trigger.Every : (task.Trigger.EverySec > 0 ? task.Trigger.EverySec.ToString() : "");
            AtBox.Text = task.Trigger.At;
            CronBox.Text = task.Trigger.Expr;
            IdleBox.Text = task.Trigger.AfterMinutes.ToString();
            HotkeyBox.Text = task.Trigger.Hotkey;
            WatchPathBox.Text = task.Trigger.WatchPath;
            WatchFilterBox.Text = string.IsNullOrWhiteSpace(task.Trigger.WatchFilter) ? "*.*" : task.Trigger.WatchFilter;
            SelectWatchEvent(task.Trigger.WatchEvent);
            FileBox.Text = task.Action.File;
            ArgsBox.Text = task.Action.Args;
            WorkDirBox.Text = task.Action.WorkDir != "" ? task.Action.WorkDir : (task.Options.WorkDir != "" ? task.Options.WorkDir : "");
            HiddenBox.IsChecked = task.Options.Hidden;
            AllowConcurrentBox.IsChecked = task.Options.AllowConcurrent;
            NotifyBox.IsChecked = task.Options.NotifyOnFailure;
            TimeoutBox.Text = task.Options.TimeoutSec.ToString();
            RetryBox.Text = task.Options.Retry.ToString();
            OnlyIdleBox.IsChecked = task.When.OnlyIdle;
            AcPowerBox.IsChecked = task.When.AcPower;
            NetworkBox.IsChecked = task.When.NetworkAvailable;
            FileExistsBox.Text = task.When.FileExists;
            FileNotExistsBox.Text = task.When.FileNotExists;
            ValidateText.Text = "";
            UpdateTriggerPanels();
            _isUpdating = false;
        }

        private void SelectTriggerTag(string tag)
        {
            tag = (tag ?? "").ToLowerInvariant();
            for (int i = 0; i < TriggerTypeBox.Items.Count; i++)
            {
                var it = TriggerTypeBox.Items[i] as ComboBoxItem;
                if (it != null && (it.Tag.ToString().ToLowerInvariant() == tag)) { TriggerTypeBox.SelectedIndex = i; return; }
            }
            // fallback map
            if (tag == "start" || tag == "boot") tag = "startup";
            for (int i = 0; i < TriggerTypeBox.Items.Count; i++)
            {
                var it = TriggerTypeBox.Items[i] as ComboBoxItem;
                if (it != null && it.Tag.ToString().ToString() == tag) { TriggerTypeBox.SelectedIndex = i; return; }
            }
            TriggerTypeBox.SelectedIndex = 0;
        }

        private void SelectWatchEvent(string evt)
        {
            evt = (evt ?? "created").ToLowerInvariant();
            for (int i = 0; i < WatchEventBox.Items.Count; i++)
            {
                var it = WatchEventBox.Items[i] as ComboBoxItem;
                if (it != null && it.Tag.ToString() == evt) { WatchEventBox.SelectedIndex = i; return; }
            }
            WatchEventBox.SelectedIndex = 0;
        }

        private void TriggerTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTriggerPanels();
        }

        private void UpdateTriggerPanels()
        {
            PanelStartup.Visibility = Visibility.Collapsed;
            PanelInterval.Visibility = Visibility.Collapsed;
            PanelDaily.Visibility = Visibility.Collapsed;
            PanelCron.Visibility = Visibility.Collapsed;
            PanelIdle.Visibility = Visibility.Collapsed;
            PanelHotkey.Visibility = Visibility.Collapsed;
            PanelWatch.Visibility = Visibility.Collapsed;
            PanelManual.Visibility = Visibility.Collapsed;
            var sel = TriggerTypeBox.SelectedItem as ComboBoxItem;
            if (sel == null) return;
            string tag = sel.Tag.ToString();
            switch (tag)
            {
                case "startup": PanelStartup.Visibility = Visibility.Visible; break;
                case "interval": PanelInterval.Visibility = Visibility.Visible; break;
                case "daily": PanelDaily.Visibility = Visibility.Visible; break;
                case "cron": PanelCron.Visibility = Visibility.Visible; break;
                case "idle": PanelIdle.Visibility = Visibility.Visible; break;
                case "hotkey": PanelHotkey.Visibility = Visibility.Visible; break;
                case "watch": PanelWatch.Visibility = Visibility.Visible; break;
                case "manual": PanelManual.Visibility = Visibility.Visible; break;
                default: break;
            }
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            SaveCurrentToTask();
            var t = new TaskDefinition { Name = "new-task-" + (_tasks.Count + 1), Enabled = true, Trigger = new TaskTrigger { Type = TaskTriggerType.Manual, RawType = "manual" }, Action = new TaskAction { File = "hello.js" }, Options = new TaskOptions { Hidden = true } };
            _tasks.Add(t);
            RefreshList();
            TaskList.SelectedItem = t;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            if (cur == null) return;
            SaveCurrentToTask();
            var copy = new TaskDefinition
            {
                Name = cur.Name + "-copy",
                Enabled = cur.Enabled,
                Trigger = new TaskTrigger { Type = cur.Trigger.Type, RawType = cur.Trigger.RawType, DelaySec = cur.Trigger.DelaySec, EverySec = cur.Trigger.EverySec, Every = cur.Trigger.Every, At = cur.Trigger.At, Expr = cur.Trigger.Expr, AfterMinutes = cur.Trigger.AfterMinutes, Hotkey = cur.Trigger.Hotkey, WatchPath = cur.Trigger.WatchPath, WatchFilter = cur.Trigger.WatchFilter, WatchEvent = cur.Trigger.WatchEvent },
                Action = new TaskAction { File = cur.Action.File, Args = cur.Action.Args, WorkDir = cur.Action.WorkDir },
                Options = new TaskOptions { Hidden = cur.Options.Hidden, TimeoutSec = cur.Options.TimeoutSec, AllowConcurrent = cur.Options.AllowConcurrent, Retry = cur.Options.Retry, NotifyOnFailure = cur.Options.NotifyOnFailure, WorkDir = cur.Options.WorkDir },
                When = new TaskCondition { OnlyIdle = cur.When.OnlyIdle, AcPower = cur.When.AcPower, NetworkAvailable = cur.When.NetworkAvailable, FileExists = cur.When.FileExists, FileNotExists = cur.When.FileNotExists }
            };
            _tasks.Add(copy);
            RefreshList();
            TaskList.SelectedItem = copy;
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            if (cur == null) return;
            if (MessageBox.Show("删除任务 \"" + cur.Name + "\" ?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _tasks.Remove(cur);
            RefreshList();
        }

        private void OnBrowseFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Filter = "脚本/可执行|*.ps1;*.js;*.py;*.bat;*.cmd;*.vbs;*.exe|所有文件|*.*";
                string scripts = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(scripts)) dlg.InitialDirectory = scripts;
                if (dlg.ShowDialog() == true)
                {
                    string full = dlg.FileName;
                    string scriptsDir = Services.ConfigService.ScriptsDirPath;
                    string rel = full;
                    try
                    {
                        if (full.StartsWith(scriptsDir, StringComparison.OrdinalIgnoreCase))
                            rel = full.Substring(scriptsDir.Length).TrimStart('\\', '/');
                    }
                    catch { }
                    FileBox.Text = rel;
                    if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
                    {
                        try { WorkDirBox.Text = System.IO.Path.GetDirectoryName(full); } catch { }
                    }
                }
            }
            catch { }
        }

        private void OnBrowseDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "选择工作目录";
                string scripts = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(scripts)) dlg.SelectedPath = scripts;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    WorkDirBox.Text = dlg.SelectedPath;
                }
            }
            catch { }
        }

        private void OnWatchBrowseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "选择监听目录";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) WatchPathBox.Text = dlg.SelectedPath;
            }
            catch { }
        }

        private TaskDefinition BuildCurrent()
        {
            var t = new TaskDefinition();
            t.Name = NameBox.Text.Trim();
            t.Enabled = EnabledBox.IsChecked == true;
            var sel = TriggerTypeBox.SelectedItem as ComboBoxItem;
            string tag = sel != null ? sel.Tag.ToString() : "startup";
            t.Trigger.RawType = tag;
            t.Trigger.Type = ParseType(tag);
            int iv;
            if (int.TryParse(DelayBox.Text.Trim(), out iv)) t.Trigger.DelaySec = iv;
            string every = EveryBox.Text.Trim();
            if (!string.IsNullOrEmpty(every))
            {
                int sec;
                if (int.TryParse(every, out sec)) t.Trigger.EverySec = sec;
                else t.Trigger.Every = every;
            }
            t.Trigger.At = AtBox.Text.Trim();
            t.Trigger.Expr = CronBox.Text.Trim();
            int idleM;
            if (int.TryParse(IdleBox.Text.Trim(), out idleM)) t.Trigger.AfterMinutes = idleM;
            t.Trigger.Hotkey = HotkeyBox.Text.Trim();
            t.Trigger.WatchPath = WatchPathBox.Text.Trim();
            t.Trigger.WatchFilter = WatchFilterBox.Text.Trim();
            var wSel = WatchEventBox.SelectedItem as ComboBoxItem;
            t.Trigger.WatchEvent = wSel != null ? wSel.Tag.ToString() : "created";
            t.Action.File = FileBox.Text.Trim();
            t.Action.Args = ArgsBox.Text.Trim();
            t.Action.WorkDir = "";
            string wd = WorkDirBox.Text.Trim();
            t.Options.WorkDir = wd;
            t.Options.Hidden = HiddenBox.IsChecked == true;
            t.Options.AllowConcurrent = AllowConcurrentBox.IsChecked == true;
            t.Options.NotifyOnFailure = NotifyBox.IsChecked == true;
            int to, rt;
            if (int.TryParse(TimeoutBox.Text.Trim(), out to)) t.Options.TimeoutSec = to;
            if (int.TryParse(RetryBox.Text.Trim(), out rt)) t.Options.Retry = rt;
            t.When.OnlyIdle = OnlyIdleBox.IsChecked == true;
            t.When.AcPower = AcPowerBox.IsChecked == true;
            t.When.NetworkAvailable = NetworkBox.IsChecked == true;
            t.When.FileExists = FileExistsBox.Text.Trim();
            t.When.FileNotExists = FileNotExistsBox.Text.Trim();
            return t;
        }

        private TaskTriggerType ParseType(string tag)
        {
            tag = tag.ToLowerInvariant();
            switch (tag)
            {
                case "startup": return TaskTriggerType.Startup;
                case "interval": return TaskTriggerType.Interval;
                case "daily": return TaskTriggerType.Daily;
                case "cron": return TaskTriggerType.Cron;
                case "sessionlock": return TaskTriggerType.SessionLock;
                case "sessionunlock": return TaskTriggerType.SessionUnlock;
                case "idle": return TaskTriggerType.Idle;
                case "manual": return TaskTriggerType.Manual;
                case "hotkey": return TaskTriggerType.Hotkey;
                case "watch": return TaskTriggerType.Watch;
                default: return TaskTriggerType.Manual;
            }
        }

        private void SaveCurrentToTask()
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            if (cur == null) return;
            var built = BuildCurrent();
            // keep same object reference for list
            cur.Name = built.Name;
            cur.Enabled = built.Enabled;
            cur.Trigger = built.Trigger;
            cur.Action = built.Action;
            cur.Options = built.Options;
            cur.When = built.When;
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var cur = BuildCurrent();
            string err = cur.Validate();
            if (err != null)
            {
                ValidateText.Text = "校验失败: " + err;
                MessageBox.Show(err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // also check whole list duplicates
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string dup = null;
                foreach (var t in _tasks)
                {
                    string n = t == TaskList.SelectedItem ? cur.Name : t.Name;
                    if (names.Contains(n)) { dup = n; break; }
                    names.Add(n);
                }
                if (dup != null)
                {
                    ValidateText.Text = "校验失败: 重名 " + dup;
                    MessageBox.Show("重名: " + dup, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    ValidateText.Text = "校验通过";
                    MessageBox.Show("校验通过", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            SaveCurrentToTask();
            // validate all
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _tasks)
            {
                string err = t.Validate();
                if (err != null) errors.Add(t.Name + ": " + err);
                if (seen.Contains(t.Name)) errors.Add("重名: " + t.Name);
                else seen.Add(t.Name);
            }
            if (errors.Count > 0)
            {
                MessageBox.Show("存在错误，无法保存:\n" + string.Join("\n", errors), "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                TaskConfigService.Save(_tasks);
                MessageBox.Show("已保存到 " + Services.ConfigService.TaskFilePath, "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshList();
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private void OnSaveReloadClick(object sender, RoutedEventArgs e)
        {
            OnSaveClick(sender, e);
            try
            {
                var app = Application.Current as ScreenLock.App;
                if (app != null && ScreenLock.App.TaskScheduler != null)
                {
                    var res = ScreenLock.App.TaskScheduler.Reload();
                    string msg = res.Errors.Count == 0 ? "已重载 " + res.Tasks.Count + " 个任务" : "重载完成 " + res.Errors.Count + " 个错误";
                    MessageBox.Show(msg, "重载", MessageBoxButton.OK, MessageBoxImage.Information);
                    try { app.Dispatcher.Invoke(new Action(() => app.RefreshTaskMenu())); } catch { }
                }
            }
            catch { }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
        }
    }
}
