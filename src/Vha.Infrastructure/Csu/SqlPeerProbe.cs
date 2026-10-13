using System;
using Vha.Core.Csu;
using Vha.Core.Model;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Csu
{
    public sealed class SqlPeerProbe : IPeerProbe
    {
        public PeerProbe Probe(NodeInfo node, int agId)
        {
            using (var c = Open(node.ConnectionString))
            using (var cmd = Cmd(c, null, "SELECT MAX(LastSCN) FROM dbo.SCN WHERE JAGroupsID = @id", 30, P("@id", agId)))
            {
                var v = cmd.ExecuteScalar();
                return new PeerProbe { Node = node, Reachable = true, Scn = v == null || v is DBNull ? 0 : Convert.ToInt32(v) };
            }
        }
    }
}
