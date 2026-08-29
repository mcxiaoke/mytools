using System;
using Microsoft.Win32;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class SessionEventTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private readonly TaskTriggerType _type;
        private bool _subscribed;

        public SessionEventTrigger(TaskDefinition task)
        {
            Task = task;
            _type = task.Trigger.Type;
        }

        public void Start()
        {
            if (_subscribed) return;
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _subscribed = true;
            }
            catch { }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                if (_type == TaskTriggerType.SessionLock && e.Reason == SessionSwitchReason.SessionLock)
                {
                    var h = Fired;
                    if (h != null) h(Task, "sessionLock");
                }
                else if (_type == TaskTriggerType.SessionUnlock && e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    var h = Fired;
                    if (h != null) h(Task, "sessionUnlock");
                }
            }
            catch { }
        }

        public void Stop()
        {
            if (!_subscribed) return;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            _subscribed = false;
        }

        public void Dispose() { Stop(); }
    }
}
