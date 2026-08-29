using System;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class ManualTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        public ManualTrigger(TaskDefinition task)
        {
            Task = task;
        }

        public void Start()
        {
            // no timer, just registered for manual invocation
        }

        public void Stop() { }

        public void Dispose() { }

        public void TriggerManual()
        {
            var h = Fired;
            if (h != null) h(Task, "manual");
        }
    }
}
