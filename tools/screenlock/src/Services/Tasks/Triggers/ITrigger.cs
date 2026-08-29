using System;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
{
    public interface ITrigger : IDisposable
    {
        TaskDefinition Task { get; }
        void Start();
        void Stop();
        event Action<TaskDefinition, string> Fired;
    }
}
