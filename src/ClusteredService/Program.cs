using System;
using System.ServiceProcess;

namespace ClusteredService
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(Vha.VersionInfo.Banner("vHA ClusteredService")); return 0; }
            if (args.Length == 2 && args[0] == "--check") return Preflight.Run(args[1]);
            if (Environment.UserInteractive)
            {
                Console.Error.WriteLine("ClusteredService.exe runs as a Windows service (install: installutil /AGName=<AG> ClusteredService.exe).\r\nDiagnostics: ClusteredService.exe --check Jelena-<AG>");
                return 2;
            }
            ServiceBase.Run(new JelenaService());
            return 0;
        }
    }
}
