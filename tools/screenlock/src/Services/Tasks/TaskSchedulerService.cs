using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenLock.Models;
using ScreenLock.Services.Tasks.Triggers;

namespace ScreenLock.Services.Tasks
{
    public class TaskSchedulerService : IDisposable
    {
        private readonly object _lock = new object();
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        private List<ITrigger> _triggers = new List<ITrigger>();
        private Dictionary<string, bool> _running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _started;
        private bool _globalEnabled = true;
        private IdleDetector _idleDetector;

        public IReadOnlyList<TaskDefinition> Tasks
        {
            get { lock (_lock) return _tasks.AsReadOnly(); }
        }

        public bool IsGlobalEnabled
        {
            get { lock (_lock) return _globalEnabled; }
        }

        public TaskSchedulerService(IdleDetector idleDetector)
        {
            _idleDetector = idleDetector;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            // read global switch from config (default true)
            try
            {
                if (ConfigService.IsPortableMode || File.Exists(ConfigService.FilePath))
                {
                    // Config.Current may not be loaded yet if Start called before Config.LoadOrCreate, fallback true
                    try { _globalEnabled = ConfigService.IsPortableMode ? true : true; } catch { _globalEnabled = true; }
                    // try read from AppSettings if available
                    // This will be corrected after App sets TasksEnabled
                }
            }
            catch { }
            try
            {
                // prefer AppSettings value if Config.Current exists
                var cur = GetCurrentTasksEnabled();
                if (cur.HasValue) _globalEnabled = cur.Value;
            }
            catch { }
            try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { }
            var result = TaskConfigService.LoadOrCreate();
            try { ScriptResolver.EnsureScriptsDir(); } catch { }
            Apply(result);
            // log
            TaskLogger.Info("system", "TaskScheduler started, tasks=" + _tasks.Count + ", errors=" + result.Errors.Count + ", globalEnabled=" + _globalEnabled);
            foreach (var e in result.Errors) TaskLogger.Warn("system", e);
            if (result.FileCreated) TaskLogger.Info("system", "tasks.json created at " + TaskConfigService.FilePath);
        }

        private bool? GetCurrentTasksEnabled()
        {
            try
            {
                // use reflection to avoid circular dep if Config not yet init, but we can try direct
                // App.Config may be null at this point
                var cfg = ScreenLock.App.Config;
                if (cfg != null && cfg.Current != null) return cfg.Current.TasksEnabled;
            }
            catch { }
            return null;
        }

        public void SetGlobalEnabled(bool enabled)
        {
            bool changed = false;
            lock (_lock) { if (_globalEnabled != enabled) { _globalEnabled = enabled; changed = true; } }
            if (!changed) return;
            if (enabled)
            {
                // re-apply current tasks to recreate triggers
                TaskLoadResult result = null;
                try { result = TaskConfigService.Load(); } catch { result = new TaskLoadResult(); }
                if (result != null) Apply(result);
                TaskLogger.Info("system", "TaskScheduler global enabled");
            }
            else
            {
                StopTriggers();
                TaskLogger.Info("system", "TaskScheduler global disabled");
            }
            // persist to config.json if possible
            try
            {
                var cfg = ScreenLock.App.Config;
                if (cfg != null && cfg.Current != null)
                {
                    cfg.Current.TasksEnabled = enabled;
                    cfg.Save();
                }
            }
            catch { }
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            StopTriggers();
            TaskLogger.Info("system", "TaskScheduler stopped");
        }

        public TaskLoadResult Reload()
        {
            var result = TaskConfigService.Load();
            // refresh global flag from config in case it was edited externally
            try
            {
                var cur = GetCurrentTasksEnabled();
                if (cur.HasValue) _globalEnabled = cur.Value;
                else
                {
                    // also try reload config file's TasksEnabled if not via App
                    // fallback: read raw config json
                    try
                    {
                        if (File.Exists(ConfigService.FilePath))
                        {
                            var txt = File.ReadAllText(ConfigService.FilePath);
                            if (txt.Contains("\"TasksEnabled\": false") || txt.Contains("\"TasksEnabled\":false"))
                                _globalEnabled = false;
                            else if (txt.Contains("\"TasksEnabled\": true"))
                                _globalEnabled = true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            Apply(result);
            TaskLogger.Info("system", "TaskScheduler reloaded, tasks=" + _tasks.Count + ", errors=" + result.Errors.Count + ", globalEnabled=" + _globalEnabled);
            foreach (var e in result.Errors) TaskLogger.Warn("system", e);
            return result;
        }

        private void Apply(TaskLoadResult result)
        {
            StopTriggers();
            lock (_lock)
            {
                _tasks = result.Tasks ?? new List<TaskDefinition>();
                _running.Clear();
            }
            if (!_globalEnabled)
            {
                lock (_lock) _triggers = new List<ITrigger>();
                TaskLogger.Info("system", "TaskScheduler disabled globally, triggers not started");
                return;
            }
            // create triggers for enabled tasks
            var newTriggers = new List<ITrigger>();
            foreach (var task in _tasks)
            {
                if (!task.Enabled) continue;
                ITrigger trig = CreateTrigger(task);
                if (trig == null) continue;
                trig.Fired += OnTriggerFired;
                newTriggers.Add(trig);
            }
            foreach (var t in newTriggers)
            {
                try { t.Start(); } catch (Exception ex) { TaskLogger.Error(t.Task.Name, "trigger start failed: " + ex.Message); }
            }
            lock (_lock) _triggers = newTriggers;
        }

        private ITrigger CreateTrigger(TaskDefinition task)
        {
            switch (task.Trigger.Type)
            {
                case TaskTriggerType.Startup: return new StartupTrigger(task);
                case TaskTriggerType.Interval: return new IntervalTrigger(task);
                case TaskTriggerType.Daily: return new DailyTrigger(task);
                case TaskTriggerType.Cron: return new CronTrigger(task);
                case TaskTriggerType.SessionLock:
                case TaskTriggerType.SessionUnlock: return new SessionEventTrigger(task);
                case TaskTriggerType.Idle: return new IdleTrigger(task, _idleDetector);
                case TaskTriggerType.Manual: return new ManualTrigger(task);
                case TaskTriggerType.Hotkey: return new HotkeyTrigger(task);
                case TaskTriggerType.Watch: return new FileWatcherTrigger(task);
                default: return new StartupTrigger(task);
            }
        }

        public IReadOnlyList<TaskDefinition> GetManualTasks()
        {
            lock (_lock)
            {
                var list = new List<TaskDefinition>();
                foreach (var t in _tasks) if (t.Trigger.Type == TaskTriggerType.Manual && t.Enabled) list.Add(t);
                return list.AsReadOnly();
            }
        }

        public bool RunManual(string name)
        {
            TaskDefinition task = null;
            lock (_lock)
            {
                foreach (var t in _tasks) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) { task = t; break; }
            }
            if (task == null) return false;
            if (!_globalEnabled) return false;
            if (!task.Enabled) return false;
            // check condition
            string skip;
            if (!TaskConditionEvaluator.ShouldRun(task, out skip))
            {
                TaskLogger.Warn(task.Name, "manual skipped: " + skip);
                return false;
            }
            Task.Run(() => ExecuteAsync(task, "manual"));
            return true;
        }

        public void RecordFailureNotify(TaskDefinition task, int code)
        {
            try
            {
                if (task.Options != null && !task.Options.NotifyOnFailure) return;
                if (code == 0) return;
                // try show balloon via App
                try
                {
                    var app = System.Windows.Application.Current as ScreenLock.App;
                    if (app != null)
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                // use reflection to call ShowBalloon if accessible, else fallback
                                var m = typeof(ScreenLock.App).GetMethod("ShowBalloonPublic", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                                if (m != null) m.Invoke(app, new object[] { "任务失败 [" + task.Name + "] exit=" + code + "，详见 logs/task-" + task.Name + ".log" });
                                else
                                {
                                    // fallback via tray
                                    System.Windows.Forms.MessageBox.Show("任务 " + task.Name + " 失败 exit=" + code, "ScreenLock", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                                }
                            }
                            catch { }
                        }));
                    }
                }
                catch { }
            }
            catch { }
        }

        private void StopTriggers()
        {
            List<ITrigger> old;
            lock (_lock) { old = _triggers; _triggers = new List<ITrigger>(); }
            foreach (var t in old)
            {
                try { t.Fired -= OnTriggerFired; } catch { }
                try { t.Stop(); } catch { }
                try { t.Dispose(); } catch { }
            }
        }

        private void OnTriggerFired(TaskDefinition task, string reason)
        {
            // dispatch to thread pool, avoid blocking trigger thread (especially UI timer)
            var t = task;
            var r = reason;
            Task.Run(() => ExecuteAsync(t, r));
        }

        private async Task ExecuteAsync(TaskDefinition task, string reason)
        {
            // condition check
            string skipReason;
            if (!TaskConditionEvaluator.ShouldRun(task, out skipReason))
            {
                TaskLogger.Info(task.Name, "skipped(" + reason + ") condition not met: " + skipReason);
                return;
            }

            bool skip = false;
            lock (_lock)
            {
                bool isRunning;
                if (_running.TryGetValue(task.Name, out isRunning) && isRunning)
                {
                    bool allow = task.Options != null && task.Options.AllowConcurrent;
                    if (!allow) skip = true;
                }
                if (!skip) _running[task.Name] = true;
            }
            if (skip)
            {
                TaskLogger.Warn(task.Name, "skipped(" + reason + ") concurrent execution not allowed");
                return;
            }
            int lastCode = 0;
            try
            {
                int retry = task.Options != null ? task.Options.Retry : 0;
                int attempt = 0;
                while (true)
                {
                    attempt++;
                    int code = 0;
                    try
                    {
                        code = await TaskRunner.RunAsync(task, reason + (attempt > 1 ? " retry#" + attempt : "")).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TaskLogger.Error(task.Name, "run exception: " + ex);
                        code = -1;
                    }
                    lastCode = code;
                    if (code == 0 || attempt > retry) break;
                    TaskLogger.Info(task.Name, "retry " + attempt + "/" + retry + " after non-zero exit " + code);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_lock) _running[task.Name] = false;
            }
            // failure notify
            try
            {
                if (lastCode != 0)
                {
                    RecordFailureNotify(task, lastCode);
                    // also keep recent list for tray
                    lock (_lock)
                    {
                        AddRecent(task.Name, lastCode);
                    }
                }
                else
                {
                    lock (_lock) AddRecent(task.Name, 0);
                }
            }
            catch { }
        }

        private List<string> _recent = new List<string>();
        private void AddRecent(string name, int code)
        {
            string entry = string.Format("{0:HH:mm:ss} {1} {2}", DateTime.Now, name, code == 0 ? "ok" : "fail:" + code);
            _recent.Insert(0, entry);
            if (_recent.Count > 10) _recent.RemoveAt(10);
        }
        public IReadOnlyList<string> GetRecent()
        {
            lock (_lock) return _recent.AsReadOnly();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            // delay a bit for system to stabilize, then catch up daily/cron
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, args) =>
            {
                try { timer.Stop(); } catch { }
                try
                {
                    lock (_lock)
                    {
                        foreach (var trig in _triggers)
                        {
                            var d = trig as DailyTrigger;
                            if (d != null) d.CheckCatchUp();
                            var c = trig as CronTrigger;
                            if (c != null) c.CheckCatchUp();
                        }
                    }
                    TaskLogger.Info("system", "resume catch-up checked");
                }
                catch { }
            };
            timer.Start();
        }

        public void Dispose() { Stop(); }
    }
}
