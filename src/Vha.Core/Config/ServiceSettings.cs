using System;

namespace Vha.Core.Config
{
    public enum OwnerUnreachablePolicy { Force, Abort }

    /// <summary>Settings read from Jelena.ini by the clustered service.</summary>
    public sealed class ServiceSettings
    {
        public string VirtualMachineName { get; private set; }
        public string AgName { get; private set; }
        public bool ReservationEnabled { get; private set; }
        public string TaskName { get; private set; }
        public string Instance { get; private set; }
        public OwnerUnreachablePolicy UnreachablePolicy { get; private set; } = OwnerUnreachablePolicy.Force;
        public TimeSpan CsuWaitTimeout { get; private set; } = TimeSpan.FromSeconds(120);
        public TimeSpan FailoverTimeout { get; private set; } = TimeSpan.FromMinutes(15);
        public int LogRetentionDays { get; private set; } = 31;
        /// <summary>Extra connection string options for the local Jelena database, e.g. "Encrypt=True;TrustServerCertificate=True".</summary>
        public string SqlOptions { get; private set; } = "";

        /// <param name="serviceName">Windows service name (Jelena-&lt;AG&gt;), key of the [AG] section that holds the AG name.</param>
        public static ServiceSettings FromIni(IniDocument ini, string serviceName)
        {
            string Required(string section, string key, string hint)
            {
                var v = ini.Get(section, key).Trim();
                if (v.Length == 0) throw new InvalidOperationException(hint + " cannot be blank, check configuration file under [" + section + "] section, key '" + key + "'");
                return v;
            }

            var s = new ServiceSettings
            {
                VirtualMachineName = Required("VIM", "VirtualMachineName", "VirtualMachineName"),
                AgName = Required("AG", serviceName, "Availability group name"),
                TaskName = Required("AG", "TaskName", "Windows Task Scheduler associated task name"),
                Instance = Required("AG", "Instance", "Local instance")
            };

            var res = Required("AG", "Reservation", "Reservation");
            if (res.Equals("Y", StringComparison.OrdinalIgnoreCase)) s.ReservationEnabled = true;
            else if (res.Equals("N", StringComparison.OrdinalIgnoreCase)) s.ReservationEnabled = false;
            else throw new InvalidOperationException("Reservation must be Y or N, found '" + res + "'");

            s.SqlOptions = ini.Get("AG", "SqlOptions").Trim().Trim(';');

            var pol = ini.Get("Failover", "OwnerUnreachablePolicy").Trim();
            if (pol.Length > 0)
            {
                if (!Enum.TryParse(pol, true, out OwnerUnreachablePolicy p) || !Enum.IsDefined(typeof(OwnerUnreachablePolicy), p))
                    throw new InvalidOperationException("OwnerUnreachablePolicy must be Force or Abort, found '" + pol + "'");
                s.UnreachablePolicy = p;
            }
            s.CsuWaitTimeout = Seconds(ini, "Failover", "CsuWaitSeconds", 120, 1, 3600);
            s.FailoverTimeout = Seconds(ini, "Failover", "FailoverTimeoutSeconds", 900, 30, 7200);
            s.LogRetentionDays = (int)Seconds(ini, "General", "LogRetentionDays", 31, 1, 3650).TotalSeconds;
            return s;
        }

        private static TimeSpan Seconds(IniDocument ini, string section, string key, int def, int min, int max)
        {
            var v = ini.Get(section, key).Trim();
            if (v.Length == 0) return TimeSpan.FromSeconds(def);
            if (!int.TryParse(v, out var n) || n < min || n > max)
                throw new InvalidOperationException(key + " must be an integer between " + min + " and " + max + ", found '" + v + "'");
            return TimeSpan.FromSeconds(n);
        }
    }
}
