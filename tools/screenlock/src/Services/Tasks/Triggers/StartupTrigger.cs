using System;
using System.Threading;
using System.Threading.Tasks;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public class StartupTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private CancellationTokenSource _cts;

        public StartupTrigger(TaskDefinition task)
        {
            Task = task;
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            int delay = Task.Trigger != null ? Task.Trigger.DelaySec : 5;
            if (delay < 0) delay = 0;
            var token = _cts.Token;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(delay * 1000, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    var h = Fired;
                    if (h != null) h(Task, "startup");
                }
                catch (System.Threading.Tasks.TaskCanceledException) { }
                catch { }
            });
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_cts != null) _cts.Dispose(); } catch { }
            _cts = null;
        }

        public void Dispose() { Stop(); }
    }
}
