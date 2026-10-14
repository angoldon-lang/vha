using System;
using System.Threading;

namespace Vha.Core.Util
{
    public static class Retry
    {
        /// <summary>
        /// Runs <paramref name="action"/> up to <paramref name="attempts"/> times, waiting <paramref name="delay"/> between attempts, but only while the failure is
        /// <paramref name="isTransient"/>. A permanent failure (wrong password, bad configuration) is rethrown immediately; the last transient failure is rethrown
        /// when the attempts are exhausted.
        /// </summary>
        public static T Run<T>(Func<T> action, int attempts, TimeSpan delay, Func<Exception, bool> isTransient, Action<int, Exception> onRetry = null, Action<TimeSpan> sleep = null)
        {
            if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));
            sleep = sleep ?? Thread.Sleep;
            for (int attempt = 1; ; attempt++)
            {
                try { return action(); }
                catch (Exception ex) when (attempt < attempts && isTransient(ex))
                {
                    onRetry?.Invoke(attempt, ex);
                    sleep(delay);
                }
            }
        }
    }
}
