using System;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class IdleTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private bool _subscribed;
        private IdleDetector _detector;

        public IdleTrigger(TaskDefinition task, IdleDetector detector)
        {
            Task = task;
            _detector = detector;
        }

        public void Start()
        {
            if (_subscribed) return;
            if (_detector == null) return;
            // use a dedicated detector per idle task if afterMinutes differs from global?
            // simplest: subscribe to global IdleDetector.ThresholdReached and filter by afterMinutes
            // If task's afterMinutes != global threshold, we use a private timer.
            int need = Task.Trigger.AfterMinutes;
            // global threshold in minutes
            int global = _detector.Threshold.TotalMinutes > 0 ? (int)_detector.Threshold.TotalMinutes : 5;
            if (need == global || need <= 0)
            {
                _detector.ThresholdReached += OnIdle;
                _subscribed = true;
            }
            else
            {
                // use polling fallback: create own IdleDetector for this task
                // To keep independent, create private detector
                var priv = new IdleDetector();
                priv.Threshold = TimeSpan.FromMinutes(need);
                priv.ShouldSuspend = _detector.ShouldSuspend;
                priv.ThresholdReached += OnIdlePrivate;
                priv.Start();
                _privateDetector = priv;
                _subscribed = true;
            }
        }

        private IdleDetector _privateDetector;

        private void OnIdle()
        {
            var h = Fired;
            if (h != null) h(Task, "idle:" + Task.Trigger.AfterMinutes + "m");
        }

        private void OnIdlePrivate()
        {
            var h = Fired;
            if (h != null) h(Task, "idle:" + Task.Trigger.AfterMinutes + "m");
            // reset private detector for next cycle
            try { if (_privateDetector != null) _privateDetector.Reset(); } catch { }
        }

        public void Stop()
        {
            if (!_subscribed) return;
            try
            {
                if (_privateDetector != null)
                {
                    _privateDetector.ThresholdReached -= OnIdlePrivate;
                    _privateDetector.Dispose();
                    _privateDetector = null;
                }
                else if (_detector != null)
                {
                    _detector.ThresholdReached -= OnIdle;
                }
            }
            catch { }
            _subscribed = false;
        }

        public void Dispose() { Stop(); }
    }
}
