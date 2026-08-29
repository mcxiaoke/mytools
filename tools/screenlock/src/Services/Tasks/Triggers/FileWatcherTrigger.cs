using System;
using System.IO;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class FileWatcherTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private FileSystemWatcher _watcher;
        private DateTime _lastFired = DateTime.MinValue;

        public FileWatcherTrigger(TaskDefinition task)
        {
            Task = task;
        }

        public void Start()
        {
            Stop();
            string path = Task.Trigger.WatchPath ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                TaskLogger.Warn(Task.Name, "watch path empty");
                return;
            }
            try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
            string filter = Task.Trigger.WatchFilter ?? "*.*";
            if (string.IsNullOrWhiteSpace(filter)) filter = "*.*";
            string evt = (Task.Trigger.WatchEvent ?? "created").Trim().ToLowerInvariant();

            string dir = path;
            // if path is file, watch its directory with filter = filename
            try
            {
                if (File.Exists(path) || (!string.IsNullOrEmpty(Path.GetExtension(path)) && !Directory.Exists(path)))
                {
                    string d = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                    {
                        dir = d;
                        filter = Path.GetFileName(path);
                    }
                }
            }
            catch { }

            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
                if (!Directory.Exists(dir))
                {
                    TaskLogger.Warn(Task.Name, "watch dir not found: " + dir);
                    return;
                }
            }

            try
            {
                _watcher = new FileSystemWatcher(dir, filter);
                _watcher.IncludeSubdirectories = false;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                // hook based on event type
                if (evt == "created" || evt == "all" || evt == "*")
                    _watcher.Created += OnEvent;
                if (evt == "changed" || evt == "all" || evt == "*")
                {
                    _watcher.Changed += OnEvent;
                    _watcher.NotifyFilter |= NotifyFilters.LastWrite;
                }
                if (evt == "deleted" || evt == "all")
                    _watcher.Deleted += OnEvent;
                if (evt == "renamed" || evt == "all")
                    _watcher.Renamed += OnRenamed;
                if (evt != "created" && evt != "changed" && evt != "deleted" && evt != "renamed" && evt != "all" && evt != "*")
                {
                    // default to created if unknown
                    _watcher.Created += OnEvent;
                }
                _watcher.EnableRaisingEvents = true;
                TaskLogger.Info(Task.Name, "watch started dir=" + dir + " filter=" + filter + " event=" + evt);
            }
            catch (Exception ex)
            {
                TaskLogger.Warn(Task.Name, "watch start failed: " + ex.Message);
            }
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            // debounce 500ms per file
            var now = DateTime.Now;
            if ((now - _lastFired).TotalMilliseconds < 500) return;
            _lastFired = now;
            var h = Fired;
            if (h != null) h(Task, "watch:" + e.ChangeType + ":" + e.Name);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            var now = DateTime.Now;
            if ((now - _lastFired).TotalMilliseconds < 500) return;
            _lastFired = now;
            var h = Fired;
            if (h != null) h(Task, "watch:renamed:" + e.Name);
        }

        public void Stop()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnEvent;
                    _watcher.Changed -= OnEvent;
                    _watcher.Deleted -= OnEvent;
                    _watcher.Renamed -= OnRenamed;
                    _watcher.Dispose();
                }
            }
            catch { }
            _watcher = null;
        }

        public void Dispose() { Stop(); }
    }
}
