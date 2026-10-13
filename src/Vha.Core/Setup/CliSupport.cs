using System;
using System.Collections.Generic;
using System.Linq;

namespace Vha.Core.Setup
{
    /// <summary>Command line options of VhaSetup (<c>--name value</c> and the flags yes, dry-run, scripts).</summary>
    public sealed class CliOptions
    {
        private readonly Dictionary<string, string> _v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "yes", "dry-run", "scripts" };
        private static readonly HashSet<string> Values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config", "node", "package", "prereq" };

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected argument '" + args[i] + "'");
                var name = args[i].Substring(2);
                if (Flags.Contains(name)) { o._v[name] = "true"; continue; }
                if (!Values.Contains(name)) throw new InvalidOperationException("Unknown option --" + name);
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("Missing value for --" + name);
                if (o._v.ContainsKey(name)) throw new InvalidOperationException("--" + name + " given twice");
                o._v[name] = args[++i];
            }
            return o;
        }

        public string Get(string name) => _v.TryGetValue(name, out var v) ? v : null;
        public bool Has(string name) => _v.ContainsKey(name);
    }

    public static class NodeResolver
    {
        /// <summary>The node of the configuration this computer is: --node, or the computer name matched against the DNS short name / VM name.</summary>
        public static NodeSettings Resolve(DeploymentConfig c, string requested, string machineName)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return c.Nodes.FirstOrDefault(n => string.Equals(n.VmName, requested.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("--node " + requested + " is not in the configuration (" + string.Join(", ", c.Nodes.Select(n => n.VmName)) + ")");
            var hits = c.Nodes.Where(n => string.Equals(n.DnsName.Split('.')[0], machineName, StringComparison.OrdinalIgnoreCase) || string.Equals(n.VmName, machineName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException("Cannot tell which node this computer (" + machineName + ") is: use --node " + string.Join(" | ", c.Nodes.Select(n => n.VmName)));
        }
    }
}
