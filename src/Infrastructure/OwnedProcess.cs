using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GameRelay.Infrastructure
{
    // A private job is populated while the root is suspended. No name-based termination,
    // breakaway flags, handle inheritance, or KILL_ON_JOB_CLOSE is used.
    public sealed class OwnedProcess : IDisposable
    {
        private IntPtr job;
        private Process root;
        public int Pid { get { return root.Id; } }
        public long StartUtc { get; private set; }
        public bool RootExited { get { return root.HasExited; } }
        public int ExitCode { get { return root.ExitCode; } }
        public bool Alive
        {
            get
            {
                var info = new JobAccounting(); int length;
                if (!QueryInformationJobObject(job, 1, ref info, Marshal.SizeOf(typeof(JobAccounting)), out length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return info.ActiveProcesses > 0;
            }
        }
        public bool TaskTreeAlive
        {
            get
            {
                if (!root.HasExited) return true;
                foreach (int pid in ProcessIds())
                {
                    try { using (var p = Process.GetProcessById(pid)) if (!p.HasExited && !WindowsRuntime.IsSharedInfrastructure(p.ProcessName) && !IsConsoleHost(p)) return true; }
                    catch (ArgumentException) { }
                }
                return false;
            }
        }
        private static bool IsConsoleHost(Process process)
        {
            return string.Equals(process.ProcessName, "conhost", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(process.MainModule.FileName, System.IO.Path.Combine(Environment.SystemDirectory, "conhost.exe"), StringComparison.OrdinalIgnoreCase);
        }
        private int[] ProcessIds()
        {
            int size = 8 + IntPtr.Size * 4096; IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int length;
                if (!QueryJobProcessIds(job, 3, buffer, size, out length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                int count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > 4096) throw new InvalidOperationException("进程树范围无法确认");
                var pids = new int[count]; for (int n = 0; n < count; n++) pids[n] = (int)Marshal.ReadIntPtr(buffer, 8 + n * IntPtr.Size).ToInt64();
                return pids;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public static string Quote(string argument)
        {
            if (argument == null || argument.IndexOf('\0') >= 0) throw new ArgumentException("参数无效");
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1);
                else b.Append('\\', slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        public static OwnedProcess Start(string executable, string[] arguments, string directory)
        {
            var owned = new OwnedProcess();
            owned.job = CreateJobObject(IntPtr.Zero, null);
            if (owned.job == IntPtr.Zero) throw new Win32Exception();
            var si = new StartupInfo(); si.cb = Marshal.SizeOf(typeof(StartupInfo));
            si.dwFlags = 1; si.wShowWindow = 7; // SW_SHOWMINNOACTIVE; application may override.
            ProcessInformation pi;
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x400 | 0x8000000, IntPtr.Zero, directory, ref si, out pi))
            { int error = Marshal.GetLastWin32Error(); owned.Dispose(); throw new Win32Exception(error); }
            try
            {
                if (!AssignProcessToJobObject(owned.job, pi.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error());
                owned.root = Process.GetProcessById(pi.dwProcessId);
                owned.StartUtc = owned.root.StartTime.ToUniversalTime().Ticks;
                if (ResumeThread(pi.hThread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return owned;
            }
            catch { TerminateProcess(pi.hProcess, 1); owned.Dispose(); throw; }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        public bool Terminate()
        {
            if (!Alive) return true;
            if (!TerminateJobObject(job, 1)) return false;
            for (int n = 0; n < 40; n++) { if (!Alive) return true; System.Threading.Thread.Sleep(50); }
            return !Alive;
        }
        public bool ContainsSharedOrUnknownProcess(string installation)
        {
            // Never terminate a job containing a shared ADB server, emulator service,
            // or any executable outside the script installation. Such jobs require review.
            int size = 8 + IntPtr.Size * 4096; IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int length;
                if (!QueryJobProcessIds(job, 3, buffer, size, out length)) return true;
                int count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > 4096) return true;
                for (int n = 0; n < count; n++)
                {
                    int pid = (int)Marshal.ReadIntPtr(buffer, 8 + n * IntPtr.Size).ToInt64();
                    try
                    {
                        using (var process = Process.GetProcessById(pid))
                        {
                            if (process.HasExited || IsConsoleHost(process)) continue;
                            string name = process.ProcessName.ToLowerInvariant();
                            if (name.Contains("adb") || name.Contains("vmware") || name.Contains("service") || name.Contains("alas") || name.Contains("wuthering")) return true;
                            if (!WindowsRuntime.IsWithin(process.MainModule.FileName, installation)) return true;
                        }
                    }
                    catch (ArgumentException) { } // process exited after the snapshot
                    catch { return true; }
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose()
        {
            // Closing manager leaves all launched tasks alive by design.
            if (root != null) { root.Dispose(); root = null; }
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb; public string reserved, desktop, title;
            public int x, y, xSize, ySize, xCount, yCount, fill, dwFlags;
            public short wShowWindow, reserved2; public IntPtr reservedPtr, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobAccounting
        { public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int type, ref JobAccounting info, int size, out int length);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        private static extern bool QueryJobProcessIds(IntPtr job, int type, IntPtr info, int size, out int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo info, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
