using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Vha.Core.Logging
{
    /// <summary>
    /// Daily log file (yyyyMMdd.log) with retention. Logging must never break a failover:
    /// every I/O error is swallowed (and counted).
    /// </summary>
    public sealed class FileLog : ILog
    {
        private readonly string _directory;
        private readonly int _retentionDays;
        private readonly Func<DateTime> _now;
        private readonly object _lock = new object();
        private string _lastPurgeDay = "";

        public int WriteFailures { get; private set; }

        public FileLog(string directory, int retentionDays = 31, Func<DateTime> now = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _retentionDays = retentionDays;
            _now = now ?? (() => DateTime.Now);
        }

        public void Debug(string message) => Write("DEBUG", message);
        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    var now = _now();
                    Directory.CreateDirectory(_directory);
                    var day = now.ToString("yyyyMMdd");
                    File.AppendAllText(Path.Combine(_directory, day + ".log"),
                        now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + level + "\t" + message + Environment.NewLine, Encoding.UTF8);
                    if (day != _lastPurgeDay) { _lastPurgeDay = day; Purge(now); }
                }
            }
            catch (Exception)
            {
                WriteFailures++;
            }
        }

        private void Purge(DateTime now)
        {
            if (_retentionDays <= 0) return;
            try
            {
                var limit = now.Date.AddDays(-_retentionDays);
                foreach (var f in Directory.GetFiles(_directory, "????????.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (DateTime.TryParseExact(name, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) && d < limit)
                    {
                        try { File.Delete(f); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
