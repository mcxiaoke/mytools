using System;
using System.Windows.Threading;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class CronTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private DispatcherTimer _timer;
        private string _expr;
        private DateTime _lastFiredMinute = DateTime.MinValue;

        public CronTrigger(TaskDefinition task)
        {
            Task = task;
            _expr = task.Trigger.Expr ?? "";
        }

        public void Start()
        {
            Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, null);
        }

        private void OnTick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            // truncate to minute
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            if (_lastFiredMinute == minute) return;
            try
            {
                if (CronHelper.IsMatch(now, _expr))
                {
                    _lastFiredMinute = minute;
                    var h = Fired;
                    if (h != null) h(Task, "cron:" + _expr);
                }
            }
            catch { }
        }

        public void CheckCatchUp() { OnTick(null, null); }

        public void Stop()
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_timer != null) _timer.Tick -= OnTick; } catch { }
            _timer = null;
        }

        public void Dispose() { Stop(); }
    }
}
