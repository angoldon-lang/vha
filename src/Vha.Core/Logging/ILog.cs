using System;

namespace Vha.Core.Logging
{
    public interface ILog
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    public sealed class CompositeLog : ILog
    {
        private readonly ILog[] _logs;
        public CompositeLog(params ILog[] logs) { _logs = logs; }
        public void Debug(string m) { foreach (var l in _logs) l.Debug(m); }
        public void Info(string m) { foreach (var l in _logs) l.Info(m); }
        public void Warn(string m) { foreach (var l in _logs) l.Warn(m); }
        public void Error(string m) { foreach (var l in _logs) l.Error(m); }
    }
}

namespace Vha.Core.Logging
{
    /// <summary>1 = errors and warnings only, 2 = info, 3 = debug (same meaning as the CsuCmd "logging level" argument).</summary>
    public sealed class LevelLog : ILog
    {
        private readonly ILog _inner; private readonly int _level;
        public LevelLog(ILog inner, int level) { _inner = inner; _level = level; }
        public void Debug(string m) { if (_level >= 3) _inner.Debug(m); }
        public void Info(string m) { if (_level >= 2) _inner.Info(m); }
        public void Warn(string m) => _inner.Warn(m);
        public void Error(string m) => _inner.Error(m);
    }
}
