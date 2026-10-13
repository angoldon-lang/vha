using System;
using System.Diagnostics;
using Vha.Core.Logging;

namespace Vha.Infrastructure
{
    /// <summary>Writes to the Application event log (source "Jelena Console Update - &lt;AG&gt;", as before). Never throws.</summary>
    public sealed class WindowsEventLog : ILog
    {
        private readonly string _source;
        private bool _ready;

        public WindowsEventLog(string source) { _source = source; }

        public void Debug(string m) => Write(m, EventLogEntryType.Information);
        public void Info(string m) => Write(m, EventLogEntryType.Information);
        public void Warn(string m) => Write(m, EventLogEntryType.Warning);
        public void Error(string m) => Write(m, EventLogEntryType.Error);

        private void Write(string message, EventLogEntryType type)
        {
            try
            {
                if (!_ready)
                {
                    if (!EventLog.SourceExists(_source)) EventLog.CreateEventSource(_source, "Application");
                    _ready = true;
                }
                if (message.Length > 30000) message = message.Substring(0, 30000);
                EventLog.WriteEntry(_source, message, type);
            }
            catch (Exception) { /* logging must not break the run */ }
        }
    }
}
