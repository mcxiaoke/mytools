using System;
using System.Windows.Threading;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class IntervalTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private DispatcherTimer _timer;
        private int _intervalSec;

        public IntervalTrigger(TaskDefinition task)
        {
            Task = task;
            _intervalSec = task.GetIntervalSeconds();
            if (_intervalSec <= 0) _intervalSec = 60;
        }

        public void Start()
        {
            Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_intervalSec) };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            var h = Fired;
            if (h != null) h(Task, "interval:" + _intervalSec + "s");
        }

        public void Stop()
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_timer != null) _timer.Tick -= OnTick; } catch { }
            _timer = null;
        }

        public void Dispose() { Stop(); }
    }
}
