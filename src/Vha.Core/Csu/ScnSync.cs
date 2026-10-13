using System.Collections.Generic;
using System.Linq;
using Vha.Core.Model;

namespace Vha.Core.Csu
{
    public sealed class PeerProbe
    {
        public NodeInfo Node { get; set; }
        public bool Reachable { get; set; }
        public int Scn { get; set; }
        public string Error { get; set; }
    }

    public sealed class ScnDecision
    {
        public bool CopyNeeded { get; set; }
        public PeerProbe Source { get; set; }
        public int ReachablePeers { get; set; }
        public int UnreachablePeers { get; set; }
    }

    public static class ScnSync
    {
        /// <summary>
        /// The reachable peer with the highest SCN wins when it is ahead of the local node.
        /// An unreachable peer is skipped: the old code aborted the whole cycle (and with it the disk inventory)
        /// as soon as one node was down.
        /// </summary>
        public static ScnDecision Decide(int localScn, IEnumerable<PeerProbe> peers)
        {
            var list = peers.ToList();
            var reachable = list.Where(p => p.Reachable).ToList();
            var best = reachable.OrderByDescending(p => p.Scn).FirstOrDefault();
            return new ScnDecision
            {
                CopyNeeded = best != null && best.Scn > localScn,
                Source = best != null && best.Scn > localScn ? best : null,
                ReachablePeers = reachable.Count,
                UnreachablePeers = list.Count - reachable.Count
            };
        }
    }
}
