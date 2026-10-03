using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using GameRelay.Core;
using GameRelay.Infrastructure;

[assembly: AssemblyTitle("GameRelay · 游序")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
namespace GameRelay.App
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { StartupLoader.LoadCore(); return Run(args); }
            catch (Exception error) { return ReportError(args, error); }
        }
        private static int ReportError(string[] args, Exception error)
        {
            string message = "GameRelay 未启动调度。请保留 data 目录及备份。\n0x" + error.HResult.ToString("X8") + " · " + error.Message;
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)) Console.Error.WriteLine(message);
            else MessageBox.Show(message, "GameRelay · 游序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\GameRelay.SingleScheduler.v1", out first))
            {
                if (!first) return 10;
                try
                {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                    if (args.Length == 2 && args[0] == "--check")
                    {
                        if (File.Exists(Path.Combine(args[1], "state.json"))) throw new IOException("检查目标已有状态文件，拒绝覆盖");
                        var checkStore = new JsonStore(Path.GetFullPath(args[1]));
                        checkStore.Save(new State()); checkStore.Load();
                        File.WriteAllText(Path.Combine(args[1], "check.txt"), "GameRelay 0.1.0 portable data roundtrip OK"); return 0;
                    }
                    if (args.Length == 2 && args[0] == "--render-preview")
                    {
                        // Offscreen, synthetic data only; no desktop clicks, scan, or task launches.
                        Directory.CreateDirectory(args[1]); var previewStore = new JsonStore(Path.Combine(args[1], "preview-data"));
                        var demo = new State();
                        for (int n = 1; n <= 5; n++) demo.Instances.Add(new ScriptInstance { Name = "方舟账号 " + n, Adapter = "MAA", Directory = @"D:\Example\Account" + n, Entry = @"D:\Example\Account" + n + @"\MAA.exe", Capability = "示例数据 · 未启用", Version = "示例" });
                        using (var runtime = new WindowsRuntime())
                        using (var form = new MainForm(new Scheduler(demo, runtime, previewStore), previewStore, true)) form.RenderPreviews(args[1]);
                        return 0;
                    }
                    if (args.Length >= 2 && args[0] == "--measure-idle")
                    {
                        Directory.CreateDirectory(args[1]); var measurementStore = new JsonStore(Path.Combine(args[1], "measurement-data"));
                        State safe = args.Length == 3 ? JsonStore.Serializer().Deserialize<State>(File.ReadAllText(args[2])) : new State();
                        foreach (var instance in safe.Instances) instance.Enabled = false;
                        safe.Schedules.Clear(); safe.Runs.Clear(); safe.Events.Clear(); safe.Paused = true;
                        using (var runtime = new WindowsRuntime())
                        using (var form = new MainForm(new Scheduler(safe, runtime, measurementStore), measurementStore, false, true))
                        { form.MeasureIdle(args[1], () => runtime.ProbeCpuMilliseconds); Application.Run(form); }
                        return 0;
                    }
                    var store = new JsonStore(directory); var state = store.Load();
                    using (var runtime = new WindowsRuntime())
                    using (var form = new MainForm(new Scheduler(state, runtime, store), store)) Application.Run(form);
                    return 0;
                }
                catch (Exception error)
                {
                    return ReportError(args, error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
