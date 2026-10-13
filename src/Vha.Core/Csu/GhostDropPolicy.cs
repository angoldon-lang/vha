using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Model;

namespace Vha.Core.Csu
{
    public enum DropVerdict { Keep, Drop }

    public sealed class DropDecision
    {
        public DropVerdict Verdict { get; set; }
        public string Reason { get; set; }
    }

    public sealed class FileProbe
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
    }

    /// <summary>
    /// Decides whether the CSU may DROP a database. The original code dropped any database that was OFFLINE or
    /// RECOVERY_PENDING and had at least one missing file, on every node and for every database of the instance:
    /// losing only the .ldf (or a transient storage outage) destroyed the surviving files and the possibility of
    /// rebuilding the log. A drop is now allowed only for the "ghost" of a managed database whose files are ALL
    /// missing, on a node that does not currently own the disks of the availability group.
    /// </summary>
    public static class GhostDropPolicy
    {
        private static readonly HashSet<string> System = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "master", "model", "msdb", "tempdb", "distribution", "Jelena", "resource" };

        public static DropDecision Evaluate(string dbName, DbState state, ICollection<string> managedDatabases,
            IReadOnlyCollection<FileProbe> files, bool nodeOwnsDisks)
        {
            if (System.Contains(dbName)) return Keep("system or configuration database");
            if (!managedDatabases.Contains(dbName, StringComparer.OrdinalIgnoreCase)) return Keep("not a database managed by this availability group");
            if (state != DbState.Offline && state != DbState.RecoveryPending) return Keep("state is " + state);
            if (nodeOwnsDisks) return Keep("this node owns the disks of the availability group: the database must be recovered, not dropped");
            if (files == null || files.Count == 0) return Keep("file list unknown");
            if (files.Any(f => f.Exists)) return Keep("at least one file is still present (" + string.Join(", ", files.Where(f => f.Exists).Select(f => f.Path)) + "): never drop a database with surviving files");
            return new DropDecision { Verdict = DropVerdict.Drop, Reason = "ghost of a managed database: all " + files.Count + " file(s) are missing and the disks are not owned by this node" };
        }

        private static DropDecision Keep(string reason) => new DropDecision { Verdict = DropVerdict.Keep, Reason = reason };
    }
}
