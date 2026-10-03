using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

class Simulation
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "echo") { File.WriteAllLines(args[1], args); return 0; }
        if (args.Length > 0 && args[0] == "child")
        {
            var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "sleep 30000") { UseShellExecute = false, CreateNoWindow = true });
            File.WriteAllText(args[1], child.Id.ToString()); Thread.Sleep(30000); return 0;
        }
        if (args.Length > 0 && args[0] == "shared-fixture")
        {
            var child = Process.Start(new ProcessStartInfo(args[1], "sleep 30000") { UseShellExecute = false, CreateNoWindow = true });
            File.WriteAllText(args[2], child.Id.ToString()); return 0;
        }
        int milliseconds = args.Length > 1 ? int.Parse(args[1]) : 100;
        Thread.Sleep(milliseconds); return args.Length > 0 && args[0] == "fail" ? 9 : 0;
    }
}
