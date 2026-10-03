using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using GameRelay.Core;

namespace GameRelay.Infrastructure
{
    public sealed class WindowsRuntime : IRuntime, IDisposable
    {
        private readonly Dictionary<string, OwnedProcess> owned = new Dictionary<string, OwnedProcess>();
        public double ProbeCpuMilliseconds { get; private set; }
        public Dictionary<string, Observation> Observe(IList<ScriptInstance> instances, IList<Run> runs)
        {
            var observations = instances.ToDictionary(i => i.Id, i => new Observation());
            var names = new HashSet<string>(instances.Select(i => Path.GetFileNameWithoutExtension(i.Entry ?? "")), StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (IsSharedInfrastructure(name)) continue;
                        // Read paths, never command lines or environment (which may contain credentials).
                        string executable;
                        try { executable = ImagePath(process.Id); }
                        catch
                        {
                            if (names.Contains(name)) foreach (var i in instances.Where(i => string.Equals(Path.GetFileNameWithoutExtension(i.Entry), name, StringComparison.OrdinalIgnoreCase)))
                                observations[i.Id].Uncertain = true;
                            continue;
                        }
                        paths.Add(executable);
                        foreach (var i in instances)
                        {
                            if (!string.IsNullOrEmpty(i.Directory) && IsWithin(executable, i.Directory)) observations[i.Id].ScriptPresent = true;
                            if (!string.IsNullOrEmpty(i.ResourceProcess) && string.Equals(executable, i.ResourceProcess, StringComparison.OrdinalIgnoreCase))
                                observations[i.Id].ResourceBusy = true;
                        }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            HashSet<int> listeners = null;
            try { listeners = new HashSet<int>(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port)); }
            catch (NetworkInformationException) { }
            var mumu = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var manager in instances.Select(i => i.MuMuManager).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            { try { mumu[manager] = JsonStore.Serializer().DeserializeObject(ReadMuMu(manager)); } catch { mumu[manager] = null; } }
            foreach (var i in instances)
            {
                var o = observations[i.Id];
                if (!string.IsNullOrEmpty(i.MuMuManager))
                {
                    object all; object details = mumu.TryGetValue(i.MuMuManager, out all) ? Json.At(all, i.MuMuIndex.ToString()) : null;
                    if (details == null || Json.At(details, "is_process_started") == null || Json.Text(Json.At(details, "index")) != i.MuMuIndex.ToString() || Json.Text(Json.At(details, "error_code")) != "0") o.Uncertain = true;
                    else o.ResourceBusy |= Json.True(Json.At(details, "is_process_started")) || Json.True(Json.At(details, "is_android_started"));
                }
                if (i.ResourcePort > 0)
                {
                    if (listeners == null || (i.ResourceHost != "127.0.0.1" && i.ResourceHost != "localhost" && i.ResourceHost != "::1")) o.Uncertain = true;
                    else o.ResourceBusy |= listeners.Contains(i.ResourcePort);
                }
                foreach (var run in runs.Where(r => r.InstanceId == i.Id && r.Active))
                {
                    OwnedProcess process;
                    if (!owned.TryGetValue(run.Id, out process)) continue;
                    try { o.OwnedAlive |= process.TaskTreeAlive; o.Failed |= process.RootExited && process.ExitCode != 0; }
                    catch { o.Uncertain = true; }
                }
                o.Detail = o.Uncertain ? "进程权限或资源探测不确定" : o.ScriptPresent ? "检测到程序；未确认任务执行状态" : o.ResourceBusy ? "资源仍占用" : "未检测到占用";
            }
            foreach (var key in owned.Keys.ToList())
            {
                if (!runs.Any(r => r.Id == key && r.Active)) { owned[key].Dispose(); owned.Remove(key); }
            }
            return observations;
        }
        public static bool IsWithin(string file, string directory)
        { return Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        public static bool IsSharedInfrastructure(string name)
        {
            return string.Equals(name, "adb", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "MuMuPlayerService", StringComparison.OrdinalIgnoreCase);
        }
        private static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new System.ComponentModel.Win32Exception();
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        public string Validate(ScriptInstance i)
        {
            if (i.ObserveOnly) return "仅观察实例";
            if (!File.Exists(i.Entry) || !Directory.Exists(i.Directory)) return "安装路径失效，请修复路径";
            if (!IsWithin(i.Entry, i.Directory)) return "入口不属于安装目录";
            if (!string.Equals(Path.GetExtension(i.Entry), ".exe", StringComparison.OrdinalIgnoreCase)) return "请配置可执行文件入口；不执行拼接 Shell 命令";
            if (!i.AutoRunVerified) return "尚未核实自动执行能力，请完成接入检查";
            if (!i.ResourceVerified || string.IsNullOrWhiteSpace(i.Target)) return "尚未核实独占资源绑定";
            if (i.ResourcePort <= 0 && string.IsNullOrEmpty(i.ResourceProcess) && string.IsNullOrEmpty(i.MuMuManager) && i.Target != "process-only:" + i.Id)
                return "缺少可核对的资源探测配置";
            if (Discovery.Fingerprint(i) != i.Fingerprint) return "上游文件已变化，请重新核对兼容能力";
            return AdapterChecks.Validate(i);
        }
        public LaunchResult Start(ScriptInstance i, Run run)
        {
            var error = Validate(i); if (error != null) throw new InvalidOperationException(error);
            var process = OwnedProcess.Start(i.Entry, i.Arguments.ToArray(), i.Directory);
            owned.Add(run.Id, process);
            return new LaunchResult { Pid = process.Pid, StartUtc = process.StartUtc, Path = i.Entry };
        }
        public bool Cleanup(ScriptInstance i, Run run)
        {
            OwnedProcess process;
            if (!owned.TryGetValue(run.Id, out process)) return false;
            // A shared/service-launched emulator is not in our private job. Its remaining
            // listener/process blocks recovery in Scheduler's independent post-cleanup probe.
            try { return !process.ContainsSharedOrUnknownProcess(i.Directory) && process.Terminate(); } catch { return false; }
        }
        private string ReadMuMu(string executable)
        {
            if (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "MuMuManager.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("资源接口失效");
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, "info -v all") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable) };
                process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(2500)) { process.Kill(); throw new IOException("资源状态查询超时"); }
                ProbeCpuMilliseconds += process.TotalProcessorTime.TotalMilliseconds;
                if (process.ExitCode != 0 || !output.Wait(500) || !errors.Wait(500)) throw new IOException("资源状态查询失败");
                if (output.Result.Length > 1024 * 1024) throw new IOException("资源状态输出过大");
                return output.Result;
            }
        }
        public void Dispose() { foreach (var process in owned.Values) process.Dispose(); owned.Clear(); }
    }
}
