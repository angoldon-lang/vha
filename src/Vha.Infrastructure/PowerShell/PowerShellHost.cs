using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Text;

namespace Vha.Infrastructure.PowerShell
{
    public sealed class PowerShellException : Exception
    {
        public PowerShellException(string message) : base(message) { }
        public PowerShellException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// One in-process runspace. Differences from the original cVMware wrapper: errors always surface as exceptions
    /// (the error text used to be accumulated in a shared ByRef string), TLS 1.2/1.3 is really enabled
    /// (the old flag was inverted), values are passed as variables and every call has a timeout.
    /// </summary>
    public sealed class PowerShellHost : IDisposable
    {
        private readonly Runspace _runspace;
        private readonly TimeSpan _defaultTimeout;

        public PowerShellHost(TimeSpan? defaultTimeout = null)
        {
            _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
            // .NET Framework < 4.7 defaults to SSL3/TLS1.0, which vCenter 7/8 and SQL Server 2022+ refuse.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288; // Tls12 | Tls13
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();
        }

        public IList<PSObject> Invoke(string script, IDictionary<string, object> variables = null, TimeSpan? timeout = null)
        {
            if (variables != null)
                foreach (var kv in variables) _runspace.SessionStateProxy.SetVariable(kv.Key, kv.Value);
            try
            {
                using (var ps = System.Management.Automation.PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddScript("$ErrorActionPreference = 'Stop'\r\n" + script);
                    var async = ps.BeginInvoke();
                    if (!async.AsyncWaitHandle.WaitOne(timeout ?? _defaultTimeout))
                    {
                        try { ps.Stop(); } catch (Exception) { }
                        throw new PowerShellException("PowerShell script timed out after " + (timeout ?? _defaultTimeout).TotalSeconds + "s: " + Excerpt(script));
                    }
                    PSDataCollection<PSObject> result;
                    try { result = ps.EndInvoke(async); }
                    catch (RuntimeException ex) { throw new PowerShellException(ex.Message + " | script: " + Excerpt(script), ex); }
                    if (ps.Streams.Error.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var e in ps.Streams.Error) sb.Append(e).Append(' ');
                        throw new PowerShellException(sb.ToString().Trim() + " | script: " + Excerpt(script));
                    }
                    return result.ToList();
                }
            }
            finally
            {
                if (variables != null)
                    foreach (var k in variables.Keys) _runspace.SessionStateProxy.SetVariable(k, null);
            }
        }

        public static string Str(PSObject o, string name)
        {
            var p = o.Properties[name];
            return p?.Value == null ? "" : Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static double Dbl(PSObject o, string name)
        {
            var p = o.Properties[name];
            return p?.Value == null ? 0 : Convert.ToDouble(p.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Int(PSObject o, string name)
        {
            var p = o.Properties[name];
            return p?.Value == null ? 0 : Convert.ToInt32(p.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Excerpt(string s) => s.Length <= 300 ? s.Replace("\r\n", " ") : s.Substring(0, 300).Replace("\r\n", " ") + "...";

        public void Dispose()
        {
            try { _runspace.Close(); } catch (Exception) { }
            _runspace.Dispose();
        }
    }
}
