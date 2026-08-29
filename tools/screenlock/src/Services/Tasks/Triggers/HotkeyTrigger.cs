using System;
using ScreenLock.Models;
using ScreenLock.Services.Tasks;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class HotkeyTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private int _hotkeyId;

        public HotkeyTrigger(TaskDefinition task)
        {
            Task = task;
        }

        public void Start()
        {
            Stop();
            string hotkey = Task.Trigger.Hotkey ?? "";
            if (string.IsNullOrWhiteSpace(hotkey)) return;
            string error;
            _hotkeyId = HotkeyService.Instance.Register(hotkey, OnHotkey, out error);
            if (_hotkeyId == 0)
            {
                TaskLogger.Warn(Task.Name, "hotkey register failed [" + hotkey + "]: " + error);
            }
            else
            {
                TaskLogger.Info(Task.Name, "hotkey registered [" + hotkey + "] id=" + _hotkeyId);
            }
        }

        private void OnHotkey()
        {
            var h = Fired;
            if (h != null) h(Task, "hotkey:" + Task.Trigger.Hotkey);
        }

        public void Stop()
        {
            if (_hotkeyId != 0)
            {
                try { HotkeyService.Instance.Unregister(_hotkeyId); } catch { }
                _hotkeyId = 0;
            }
        }

        public void Dispose() { Stop(); }
    }
}
