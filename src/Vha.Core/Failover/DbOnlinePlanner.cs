using Vha.Core.Model;

namespace Vha.Core.Failover
{
    public enum DbAction { Attach, SetOnline, None, Fail }

    public sealed class DbPlan
    {
        public DbAction Action { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// What to do with a managed database when the disks have arrived. The original code only restored ACLs when the
    /// database already existed in sys.databases (so an OFFLINE database was never brought online).
    /// </summary>
    public static class DbOnlinePlanner
    {
        public static DbPlan Plan(bool existsInInstance, DbState? state, bool describedInJDBFiles)
        {
            if (!existsInInstance)
            {
                return describedInJDBFiles
                    ? new DbPlan { Action = DbAction.Attach, Reason = "database not in the instance, attaching from JDBFiles" }
                    : new DbPlan { Action = DbAction.Fail, Reason = "database is not in the instance and has no files described in dbo.JDBFiles" };
            }
            switch (state ?? DbState.Online)
            {
                case DbState.Online: return new DbPlan { Action = DbAction.None, Reason = "already online" };
                case DbState.Offline:
                case DbState.RecoveryPending:
                    return new DbPlan { Action = DbAction.SetOnline, Reason = "database present but " + state };
                case DbState.Restoring:
                case DbState.Recovering:
                    return new DbPlan { Action = DbAction.None, Reason = "database is " + state + ", waiting for SQL Server" };
                default:
                    return new DbPlan { Action = DbAction.Fail, Reason = "database is in state " + state + ", manual intervention required" };
            }
        }
    }
}
