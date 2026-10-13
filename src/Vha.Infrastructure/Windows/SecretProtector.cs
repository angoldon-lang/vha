using System;
using System.Security.Cryptography;
using System.Text;

namespace Vha.Infrastructure
{
    /// <summary>
    /// Machine scoped DPAPI protection for the vCenter password stored in JSettings
    /// ("-Password dpapi:&lt;base64&gt;"). Nodes of a cluster are different machines: protect the value on each node
    /// and store it in that node's local Jelena database (JSettings is not replicated).
    /// </summary>
    public static class SecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vha.JSettings.v1");

        public static string Protect(string plain)
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
            return "dpapi:" + Convert.ToBase64String(data);
        }

        /// <summary>Input is the base64 text WITHOUT the "dpapi:" prefix.</summary>
        public static string Unprotect(string base64)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.LocalMachine)); }
            catch (CryptographicException ex) { throw new FormatException("Cannot decrypt the DPAPI protected password on this machine (it must be protected on the same node): " + ex.Message, ex); }
        }
    }
}
