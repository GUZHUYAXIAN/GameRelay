using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GameRelay.Core;
using GameRelay.Infrastructure;

class Diagnostics
{
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: Diagnostics.exe <scan-root> <private-output-directory>"); return 2; }
        try
        {
            Directory.CreateDirectory(args[1]);
            using (var process = Process.GetCurrentProcess())
            {
                var cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew(); int count = 0;
                var found = Discovery.Scan(new[] { args[0] }, CancellationToken.None, (n, label) => count = n);
                watch.Stop(); process.Refresh();
                var report = new { ElapsedMilliseconds = watch.ElapsedMilliseconds, CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    WorkingSetMiB = process.WorkingSet64 / 1048576.0, PeakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, VisitedDirectories = count, Instances = found.Count };
                File.WriteAllText(Path.Combine(args[1], "scan-performance.json"), JsonStore.Serializer().Serialize(report));
                var state = new State(); state.Instances.AddRange(found); new JsonStore(Path.Combine(args[1], "inventory")).Save(state);
                Console.WriteLine(JsonStore.Serializer().Serialize(report)); return 0;
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name); return 1; }
    }
}
