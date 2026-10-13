using System;
using Microsoft.Win32.TaskScheduler;
using Vha.Core.Failover;

namespace Vha.Infrastructure
{
    /// <summary>Uses Task.Enabled (not Definition + RegisterChanges, which would re-register the task and drop its stored password).</summary>
    public sealed class TaskSchedulerCsuTask : ICsuTask
    {
        private readonly string _name;
        public TaskSchedulerCsuTask(string taskName) { _name = taskName; }

        private T With<T>(Func<Microsoft.Win32.TaskScheduler.Task, T> f)
        {
            using (var ts = new TaskService())
            {
                var t = ts.FindTask(_name, true) ?? throw new InvalidOperationException("Scheduled task '" + _name + "' not found (key TaskName in Jelena.ini)");
                try { return f(t); } finally { t.Dispose(); }
            }
        }

        public void Disable() => With<object>(t => { t.Enabled = false; return null; });
        public bool IsRunning() => With(t => t.State == TaskState.Running);
        public void Stop() => With<object>(t => { t.Stop(); return null; });
        public void EnableAndRun() => With<object>(t => { t.Enabled = true; t.Run(); return null; });

        public bool Exists() { using (var ts = new TaskService()) { var t = ts.FindTask(_name, true); t?.Dispose(); return t != null; } }
    }
}
