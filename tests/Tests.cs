using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GameRelay.Core;
using GameRelay.Infrastructure;

class Tests
{
    sealed class Store : IStateStore { public int Writes; public void Save(State state) { Writes++; } }
    sealed class FaultStore : IStateStore
    {
        public JsonStore Durable;
        public void Save(State state) { if (state.Runs.Any(r => r.State == "Running")) throw new IOException("injected post-launch write failure"); Durable.Save(state); }
    }
    sealed class Runtime : IRuntime
    {
        public readonly Dictionary<string, Observation> Status = new Dictionary<string, Observation>();
        public readonly List<string> Starts = new List<string>(); public int Kills; public bool CanClean = true;
        public Dictionary<string, Observation> Observe(IList<ScriptInstance> instances, IList<Run> runs)
        { foreach (var i in instances) if (!Status.ContainsKey(i.Id)) Status.Add(i.Id, new Observation()); return Status; }
        public string Validate(ScriptInstance i) { return null; }
        public LaunchResult Start(ScriptInstance i, Run run)
        { Starts.Add(i.Id); Status[i.Id] = new Observation { ScriptPresent = true, OwnedAlive = true }; return new LaunchResult { Pid = 100 + Starts.Count, Path = i.Entry, StartUtc = run.StartedUtc }; }
        public bool Cleanup(ScriptInstance i, Run run) { Kills++; if (CanClean) Status[i.Id] = new Observation(); return CanClean; }
        public void Exit(string id) { Status[id] = new Observation(); }
    }
    static int passed;
    static long now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc).Ticks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
    static State Make(int count)
    {
        var s = new State();
        for (int n = 0; n < count; n++) s.Instances.Add(new ScriptInstance { Name = "模拟账号" + n, Target = "target" + n, Enabled = true, Order = n });
        return s;
    }
    static Scheduler QueueAll(State s, Runtime r)
    { var e = new Scheduler(s, r, new Store()); foreach (var i in s.Instances.Where(i => !i.ObserveOnly)) e.Enqueue(i.Id, now); return e; }
    static Schedule Plan(ScriptInstance i, long created)
    { return new Schedule { InstanceId = i.Id, CreatedUtc = created, CursorUtc = created, Enabled = true, TimeZoneId = "UTC", Weekdays = Enumerable.Range(0, 7).ToList(), Minutes = new List<int> { 60, 120 } }; }
    static int Main()
    {
        try
        {
            Test("five independent accounts refill immediately", delegate {
                var s = Make(5); var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now);
                Check(r.Starts.Count == 3 && s.Runs.Count(x => x.Pending) == 2, "normal cap");
                r.Exit(s.Instances[1].Id); e.Tick(now + 1);
                Check(r.Starts.Count == 4 && r.Starts[3] == s.Instances[3].Id, "refill/order");
                Check(s.Runs[1].State == "ExitedUnknown", "exit must not imply success");
            });
            Test("protection lowers limit without termination", delegate {
                var s = Make(5); var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); s.Protection = true; e.Tick(now + 1);
                Check(r.Kills == 0 && r.Starts.Count == 3, "no preemption");
                r.Exit(s.Instances[0].Id); r.Exit(s.Instances[1].Id); e.Tick(now + 2); Check(r.Starts.Count == 3, "no spare slot");
                r.Exit(s.Instances[2].Id); e.Tick(now + 3); Check(r.Starts.Count == 4 && e.Occupied == 1, "protected refill");
            });
            Test("Alas excluded and never queued", delegate {
                var s = Make(4); s.Instances[0].ObserveOnly = true; var r = new Runtime(); r.Status[s.Instances[0].Id] = new Observation { ScriptPresent = true };
                var e = QueueAll(s, r); e.Tick(now); Check(r.Starts.Count == 3 && e.Occupied == 3, "observer cap");
            });
            Test("external runs cover due queue and cannot be cleaned", delegate {
                var s = Make(4); var r = new Runtime(); var e = QueueAll(s, r); r.Status[s.Instances[0].Id] = new Observation { ScriptPresent = true };
                e.Tick(now); Check(r.Starts.Count == 2 && s.Runs[0].State == "ExternalCovered", "external counted");
                r.Exit(s.Instances[0].Id); e.Tick(now + 1); Check(r.Starts.Count == 3 && r.Kills == 0, "external no duplicate");
            });
            Test("same target mutual exclusion", delegate {
                var s = Make(2); s.Instances[1].Target = s.Instances[0].Target; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now);
                Check(r.Starts.Count == 1 && s.Runs[1].Pending, "exclusive target");
            });
            Test("residual emulator retains capacity", delegate {
                var s = Make(2); s.NormalLimit = 1; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now);
                r.Status[s.Instances[0].Id] = new Observation { ResourceBusy = true }; e.Tick(now + 1);
                Check(r.Starts.Count == 1 && s.Runs[0].State == "Residual", "residual counted");
                r.Exit(s.Instances[0].Id); e.Tick(now + 2); Check(r.Starts.Count == 2, "released refill");
            });
            Test("pause inhibits starts and retries without killing", delegate {
                var s = Make(2); s.NormalLimit = 1; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); s.Paused = true;
                e.Tick(now + 1); Check(r.Kills == 0, "pause killed task"); r.Exit(s.Instances[0].Id); e.Tick(now + 2); Check(r.Starts.Count == 1, "paused started");
            });
            Test("timeout retries exactly once", delegate {
                var s = Make(1); s.Instances[0].TimeoutMinutes = 1; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now);
                e.Tick(now + TimeSpan.FromMinutes(1).Ticks); Check(r.Starts.Count == 2 && s.Runs[0].Attempt == 1, "first retry");
                e.Tick(now + TimeSpan.FromMinutes(2).Ticks); Check(r.Starts.Count == 2 && s.Runs[0].State == "Failed" && r.Kills == 2, "retry limit");
                e.Enqueue(s.Instances[0].Id, now + TimeSpan.FromMinutes(3).Ticks); e.Tick(now + TimeSpan.FromMinutes(3).Ticks); Check(r.Starts.Count == 3, "later round disabled");
            });
            Test("cleanup failure blocks retry and same instance", delegate {
                var s = Make(1); s.Instances[0].TimeoutMinutes = 1; var r = new Runtime { CanClean = false }; var e = QueueAll(s, r); e.Tick(now);
                e.Tick(now + TimeSpan.FromMinutes(2).Ticks); e.Enqueue(s.Instances[0].Id, now + 1); e.Tick(now + TimeSpan.FromMinutes(3).Ticks);
                Check(r.Starts.Count == 1 && s.Runs[0].State == "NeedsAttention", "unsafe retry");
            });
            Test("timeout disabled", delegate {
                var s = Make(1); s.Instances[0].TimeoutMinutes = 0; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); e.Tick(now + TimeSpan.FromDays(100).Ticks); Check(r.Kills == 0, "disabled timeout");
            });
            Test("offline years merge one debt per schedule", delegate {
                var s = Make(2); var r = new Runtime(); s.Paused = true;
                s.Schedules.Add(Plan(s.Instances[0], now)); s.Schedules.Add(Plan(s.Instances[1], now));
                var e = new Scheduler(s, r, new Store()); var later = now + TimeSpan.FromDays(3650).Ticks; e.Tick(later); e.Tick(later);
                Check(s.Runs.Count == 2 && s.Runs.All(x => x.Source == "合并补跑"), "missed merge");
                e.Tick(later + TimeSpan.FromDays(2).Ticks); Check(s.Runs.Count == 2, "pending debt duplicated");
            });
            Test("new schedule creates no historical debt / clock rollback", delegate {
                var s = Make(1); s.Paused = true; s.Schedules.Add(Plan(s.Instances[0], now)); var e = new Scheduler(s, new Runtime(), new Store());
                e.Tick(now); Check(s.Runs.Count == 0, "historical debt"); e.Tick(now + TimeSpan.FromHours(2).Ticks); e.Tick(now - TimeSpan.FromDays(3).Ticks); e.Tick(now + TimeSpan.FromHours(2).Ticks);
                Check(s.Runs.Count == 1, "clock duplicate");
            });
            Test("different execution profiles not merged", delegate {
                var s = Make(1); s.Paused = true; var a = Plan(s.Instances[0], now); var b = Plan(s.Instances[0], now); b.Profile = "second"; s.Schedules.Add(a); s.Schedules.Add(b);
                var e = new Scheduler(s, new Runtime(), new Store()); e.Tick(now + TimeSpan.FromDays(2).Ticks); Check(s.Runs.Count == 2, "profile debt merged");
            });
            Test("restart never inherits ownership or retry allowance", delegate {
                var s = Make(1); var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); s.Runs[0].Attempt = 1;
                var restarted = new Scheduler(s, r, new Store()); restarted.Tick(now + TimeSpan.FromDays(1).Ticks);
                Check(s.Runs[0].State == "NeedsAttention" && s.Runs[0].Attempt == 1 && r.Kills == 0 && r.Starts.Count == 1, "recovery ownership");
            });
            Test("stop prevents all future scheduling", delegate {
                var s = Make(2); var r = new Runtime(); var e = QueueAll(s, r); e.Stop(); e.Tick(now); Check(r.Starts.Count == 0 && r.Kills == 0, "stopped scheduler");
            });
            Test("ambiguous external process conservatively blocks", delegate {
                var s = Make(1); var r = new Runtime(); r.Status[s.Instances[0].Id] = new Observation { Uncertain = true }; var e = QueueAll(s, r); e.Tick(now); Check(r.Starts.Count == 0, "uncertainty launched");
            });
            Test("atomic store roundtrip backup and corruption fail closed", delegate {
                var dir = Path.Combine(Path.GetTempPath(), "GameRelay-tests-" + Guid.NewGuid().ToString("N")); var store = new JsonStore(dir); var s = Make(1); store.Save(s); s.Protection = true; store.Save(s);
                Check(new JsonStore(dir).Load().Protection && File.Exists(store.Path + ".bak"), "atomic roundtrip");
                File.WriteAllText(store.Path, "broken"); bool rejected = false; try { new JsonStore(dir).Load(); } catch { rejected = true; } Check(rejected, "silent old state replay");
            });
            Test("retry persisted to disk survives a fresh scheduler", delegate {
                var s = Make(1); s.Paused = true; var r = new Runtime(); var e = QueueAll(s, r); s.Runs[0].Attempt = 1; s.Runs[0].State = "RetryQueued";
                string dir = Path.Combine(Path.GetTempPath(), "GameRelay-recovery-" + Guid.NewGuid().ToString("N")); var store = new JsonStore(dir); store.Save(s);
                var restored = new JsonStore(dir).Load(); var fresh = new Scheduler(restored, r, store); fresh.Tick(now); Check(r.Starts.Count == 0 && restored.Runs[0].Attempt == 1, "retry allowance reset");
                restored.Paused = false; fresh.Tick(now); Check(r.Starts.Count == 1 && restored.Runs[0].Attempt == 1, "retry not resumed");
            });
            Test("post-launch persistence failure leaves durable intent and no second start", delegate {
                var s = Make(2); var r = new Runtime(); string dir = Path.Combine(Path.GetTempPath(), "GameRelay-crash-" + Guid.NewGuid().ToString("N"));
                var store = new JsonStore(dir); var e = new Scheduler(s, r, new FaultStore { Durable = store }); foreach (var i in s.Instances) e.Enqueue(i.Id, now);
                bool failed = false; try { e.Tick(now); } catch (IOException) { failed = true; }
                Check(failed && r.Starts.Count == 1, "continued after write failure");
                var recovered = new JsonStore(dir).Load(); Check(recovered.Runs.Count(x => x.State == "Starting") == 1, "intent missing");
                recovered.Paused = true; var restarted = new Scheduler(recovered, r, store); restarted.Tick(now + 1);
                Check(r.Starts.Count == 1 && recovered.Runs.Any(x => x.State == "NeedsAttention"), "crash window duplicated launch");
            });
            Test("manual and scheduled same profile coalesce", delegate {
                var s = Make(1); s.Paused = true; var r = new Runtime(); var e = QueueAll(s, r); s.Schedules.Add(Plan(s.Instances[0], now)); e.Tick(now + TimeSpan.FromDays(1).Ticks);
                Check(s.Runs.Count == 1 && s.Events.Any(x => x.Kind == "合并"), "manual conflict duplicated");
            });
            Test("same-time priority is independent of registration order", delegate {
                var s = Make(3); s.NormalLimit = 1; s.Instances[2].Order = -1; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); Check(r.Starts.Single() == s.Instances[2].Id, "priority ignored");
            });
            Test("cancelling queue allows configuration repair", delegate {
                var s = Make(1); s.Paused = true; var r = new Runtime(); var e = QueueAll(s, r); e.CancelQueued(new[] { s.Runs[0].Id }, now); s.Paused = false; e.Tick(now);
                Check(r.Starts.Count == 0 && s.Runs[0].State == "Cancelled", "cancel failed"); e.Enqueue(s.Instances[0].Id, now + 1); e.Tick(now + 1); Check(r.Starts.Count == 1, "new round blocked");
            });
            Test("external coverage at schedule time is terminal", delegate {
                var s = Make(1); var r = new Runtime(); s.Schedules.Add(Plan(s.Instances[0], now)); r.Status[s.Instances[0].Id] = new Observation { ScriptPresent = true };
                var e = new Scheduler(s, r, new Store()); e.Tick(now + TimeSpan.FromHours(1).Ticks); r.Exit(s.Instances[0].Id); e.Tick(now + TimeSpan.FromHours(1).Ticks + 1);
                Check(s.Runs.Count == 1 && s.Runs[0].State == "ExternalCovered" && r.Starts.Count == 0, "external ran twice");
            });
            Test("retry remains queued under pause and protection", delegate {
                var s = Make(1); s.Instances[0].TimeoutMinutes = 1; var r = new Runtime(); var e = QueueAll(s, r); e.Tick(now); s.Paused = true; s.Protection = true;
                e.Tick(now + TimeSpan.FromMinutes(2).Ticks); Check(s.Runs[0].State == "RetryQueued" && r.Starts.Count == 1, "retry ignored pause");
                s.Paused = false; e.Tick(now + TimeSpan.FromMinutes(3).Ticks); Check(r.Starts.Count == 2, "retry missing");
            });
            Test("DST nonexistent times skipped and overlap mapped once", delegate {
                var s = Plan(Make(1).Instances[0], new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc).Ticks); s.TimeZoneId = "Eastern Standard Time"; s.Minutes = new List<int> { 150 };
                var next = Planner.Next(s, s.CreatedUtc); Check(next == new DateTime(2026, 3, 9, 6, 30, 0, DateTimeKind.Utc).Ticks, "DST gap");
                s.CreatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).Ticks; s.Minutes = new List<int> { 90 };
                next = Planner.Next(s, s.CreatedUtc); Check(next == new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc).Ticks, "DST overlap");
            });
            Test("retention preserves active debt and bounds completed history", delegate {
                var s = Make(1); s.Retention = 100; var pending = new Run { Id = "pending", InstanceId = s.Instances[0].Id, State = "Queued" }; s.Runs.Add(pending);
                for (int n = 0; n < 130; n++) { var done = new Run { Id = n.ToString(), State = "ExitedUnknown", DueUtc = n }; s.Runs.Add(done); s.Record(now, "退出", done, "结果未确认"); }
                Check(s.Runs.Contains(pending) && s.Runs.Count == 101 && s.Events.Count == 100, "retention lost debt");
            });
            ProcessTests();
            Console.WriteLine("ALL PASS: " + passed); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void ProcessTests()
    {
        string original = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Simulation.exe");
        string dir = Path.Combine(Path.GetTempPath(), "GameRelay 中文 空格 " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "模拟 程序.exe"); File.Copy(original, exe);
        Test("Windows argv quoting Chinese spaces quotes trailing slashes", delegate {
            string output = Path.Combine(dir, "参数.txt"); string[] values = { "echo", output, "中文 空格", "", "end\\", "quote\"value", "x\\\"z", "$() & | literal" };
            using (var process = OwnedProcess.Start(exe, values, dir)) { for (int n = 0; n < 100 && process.Alive; n++) Thread.Sleep(20); Check(!process.Alive, "echo timeout"); }
            Check(File.ReadAllLines(output).SequenceEqual(values), "argv changed");
        });
        Test("owned job kills only its tree; external same-name survives", delegate {
            using (var external = Process.Start(new ProcessStartInfo(exe, "sleep 30000") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    string pidFile = Path.Combine(dir, "child.pid");
                    using (var owned = OwnedProcess.Start(exe, new[] { "child", pidFile }, dir))
                    {
                        for (int n = 0; n < 100 && !File.Exists(pidFile); n++) Thread.Sleep(20);
                        Check(File.Exists(pidFile), "child missing"); int pid = int.Parse(File.ReadAllText(pidFile));
                        Check(owned.Terminate() && !owned.Alive, "tree alive"); Check(!external.HasExited, "external killed");
                        bool childAlive = false; try { using (var child = Process.GetProcessById(pid)) childAlive = !child.HasExited; } catch (ArgumentException) { }
                        Check(!childAlive, "child survived cleanup");
                    }
                }
                finally { if (!external.HasExited) { external.Kill(); external.WaitForExit(); } }
            }
        });
        Test("closing manager job handle leaves task alive", delegate {
            var owned = OwnedProcess.Start(exe, new[] { "sleep", "30000" }, dir); int pid = owned.Pid; owned.Dispose();
            using (var survivor = Process.GetProcessById(pid)) { Check(!survivor.HasExited, "close killed child"); survivor.Kill(); survivor.WaitForExit(); }
        });
        Test("Windows runtime sees external installation and changed fingerprint", delegate {
            var instance = new ScriptInstance { Directory = dir, Entry = exe, Adapter = "Custom", Enabled = true, AutoRunVerified = true, ResourceVerified = true };
            instance.Target = "process-only:" + instance.Id; instance.Fingerprint = Discovery.Fingerprint(instance);
            using (var runtime = new WindowsRuntime())
            using (var external = Process.Start(new ProcessStartInfo(exe, "sleep 30000") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    var observed = runtime.Observe(new[] { instance }, new Run[0]); Check(observed[instance.Id].ScriptPresent && !observed[instance.Id].OwnedAlive, "external identity wrong");
                    Check(runtime.Validate(instance) == null, "valid custom instance rejected");
                    File.WriteAllText(Path.Combine(dir, "interface.json"), "{}"); Check(runtime.Validate(instance) != null, "update missed");
                }
                finally { if (!external.HasExited) { external.Kill(); external.WaitForExit(); } }
            }
        });
        Test("scan detects separate installations and cancellation", delegate {
            string scan = Path.Combine(dir, "scan"); Directory.CreateDirectory(scan);
            for (int n = 0; n < 2; n++) { string item = Path.Combine(scan, "账号 " + n); Directory.CreateDirectory(item); File.Copy(original, Path.Combine(item, "MAA.exe")); File.WriteAllText(Path.Combine(item, "MaaCore.dll"), "fixture"); }
            var found = Discovery.Scan(new[] { scan }, CancellationToken.None, null); Check(found.Count == 2 && found.Select(i => i.Id).Distinct().Count() == 2 && found.All(i => !i.Enabled && !i.AutoRunVerified), "discovery merged or enabled");
            var cancel = new CancellationTokenSource(); cancel.Cancel(); bool stopped = false;
            try { Discovery.Scan(new[] { scan }, cancel.Token, null); } catch (OperationCanceledException) { stopped = true; } Check(stopped, "scan did not cancel");
        });
        Test("shared ADB fixture is never killed and does not retain task slot", delegate {
            string scope = Path.Combine(dir, "shared fixture"); Directory.CreateDirectory(scope);
            string runner = Path.Combine(scope, "runner.exe"), adb = Path.Combine(scope, "adb.exe"), pidFile = Path.Combine(scope, "shared.pid");
            File.Copy(original, runner); File.Copy(original, adb);
            var i = new ScriptInstance { Entry = runner, Directory = scope, Adapter = "Custom", Enabled = true, AutoRunVerified = true, ResourceVerified = true, Arguments = new List<string> { "shared-fixture", adb, pidFile } };
            i.Target = "process-only:" + i.Id; i.Fingerprint = Discovery.Fingerprint(i); var run = new Run { Id = "shared", InstanceId = i.Id, State = "Running" };
            using (var runtime = new WindowsRuntime())
            {
                runtime.Start(i, run);
                for (int n = 0; n < 100 && !File.Exists(pidFile); n++) Thread.Sleep(20);
                Check(File.Exists(pidFile), "shared fixture missing");
                using (var shared = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))))
                {
                    try
                    {
                        Thread.Sleep(150); Check(!runtime.Cleanup(i, run) && !shared.HasExited, "shared server killed");
                        var observation = runtime.Observe(new[] { i }, new[] { run })[i.Id];
                        Check(!observation.OwnedAlive && !observation.ScriptPresent, "shared infrastructure occupied task slot: name=" + shared.ProcessName + ", owned=" + observation.OwnedAlive + ", present=" + observation.ScriptPresent);
                    }
                    finally { if (!shared.HasExited) { shared.Kill(); shared.WaitForExit(); } }
                }
            }
        });
        Test("production cleanup contains only owned task and preserves external process", delegate {
            string scope = Path.Combine(dir, "cleanup fixture"); Directory.CreateDirectory(scope); string runner = Path.Combine(scope, "runner.exe"); File.Copy(original, runner);
            var i = new ScriptInstance { Entry = runner, Directory = scope, Adapter = "Custom", Enabled = true, AutoRunVerified = true, ResourceVerified = true, Arguments = new List<string> { "sleep", "30000" } };
            i.Target = "process-only:" + i.Id; i.Fingerprint = Discovery.Fingerprint(i); var run = new Run { Id = "cleanup", InstanceId = i.Id, State = "Running" };
            using (var runtime = new WindowsRuntime())
            using (var external = Process.Start(new ProcessStartInfo(runner, "sleep 30000") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try { runtime.Start(i, run); Thread.Sleep(100); Check(runtime.Cleanup(i, run) && !external.HasExited, "production cleanup scope"); }
                finally { if (!external.HasExited) { external.Kill(); external.WaitForExit(); } }
            }
        });
        Test("actual manager single-instance check across process and portable data roundtrip", delegate {
            string manager = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameRelay.exe"), data = Path.Combine(dir, "portable data");
            bool created; using (var mutex = new Mutex(true, @"Local\GameRelay.SingleScheduler.v1", out created))
            {
                Check(created, "another manager is running; close it before this test");
                using (var second = Process.Start(new ProcessStartInfo(manager, "--check " + OwnedProcess.Quote(data)) { UseShellExecute = false, CreateNoWindow = true }))
                { Check(second.WaitForExit(5000) && second.ExitCode == 10 && !Directory.Exists(data), "second manager ran"); }
                mutex.ReleaseMutex();
            }
            using (var first = Process.Start(new ProcessStartInfo(manager, "--check " + OwnedProcess.Quote(data)) { UseShellExecute = false, CreateNoWindow = true }))
            { Check(first.WaitForExit(5000) && first.ExitCode == 0 && File.Exists(Path.Combine(data, "check.txt")), "portable data check"); }
        });
        Test("renamed MFA entry is discovered via metadata and keeps its own configuration", delegate {
            string scope = Path.Combine(dir, "MFA 客户端"); Directory.CreateDirectory(scope); Directory.CreateDirectory(Path.Combine(scope, "config"));
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MfaFixture.exe"), Path.Combine(scope, "CustomGame.exe"));
            File.WriteAllText(Path.Combine(scope, "interface.json"), "{\"version\":\"fixture\"}");
            string config = Path.Combine(scope, "config", "config.json");
            File.WriteAllText(config, "{\"BeforeTask\":\"StartupSoftwareAndScript\",\"AfterTask\":\"CloseEmulatorAndMFA\"}");
            var i = Discovery.Detect(scope); Check(i.Adapter == "MFAAvalonia" && Path.GetFileName(i.Entry) == "CustomGame.exe" && i.Arguments.SequenceEqual(new[] { "-c", "Default" }), "renamed client not recognized");
            Check(AdapterChecks.Validate(i) == null, "legacy direct-run config not recognized");
            i.Arguments.Add("-d"); Check(AdapterChecks.Validate(i) != null, "MaaPiCli flag mixed into GUI");
            i.Arguments.RemoveAt(i.Arguments.Count - 1); File.WriteAllText(config, "{\"BeforeTask\":\"StartupSoftware\",\"AfterTask\":\"CloseMFA\"}");
            Check(AdapterChecks.Validate(i) != null, "open GUI incorrectly accepted as execution");
        });
        Test("MAA readiness guards global ADB, builtin timer, fixed binding and automatic execution", delegate {
            string scope = Path.Combine(dir, "MAA config fixture"); Directory.CreateDirectory(scope); Directory.CreateDirectory(Path.Combine(scope, "config"));
            string path = Path.Combine(scope, "config", "gui.new.json");
            string template = "{\"Current\":\"Default\",\"Configurations\":{\"Default\":{\"Gui\":{\"StartUpSettings\":{\"RunDirectly\":true},\"PostActions\":\"ExitEmulator, ExitSelf\",\"ConnectSettings\":{\"Address\":\"127.0.0.1:20000\",\"AlwaysAutoDetect\":false,\"AllowAdbHardRestart\":false,\"KillAdbOnExit\":false}}}},\"Timers\":{\"List\":[{\"IsEnabled\":false}]}}";
            var i = new ScriptInstance { Directory = scope, Adapter = "MAA", Profile = "Default", ResourceHost = "127.0.0.1", ResourcePort = 20000, Arguments = new List<string> { "--config", "Default" } };
            File.WriteAllText(path, template); Check(AdapterChecks.Validate(i) == null, "valid MAA settings rejected");
            foreach (string key in new[] { "AlwaysAutoDetect", "AllowAdbHardRestart", "KillAdbOnExit", "IsEnabled" })
            { File.WriteAllText(path, template.Replace("\"" + key + "\":false", "\"" + key + "\":true")); Check(AdapterChecks.Validate(i) != null, "unsafe setting accepted: " + key); }
            File.WriteAllText(path, template.Replace("\"RunDirectly\":true", "\"RunDirectly\":false")); Check(AdapterChecks.Validate(i) != null, "GUI-only startup accepted");
        });
    }
}
