using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GameRelay.Core;

namespace GameRelay.Infrastructure
{
    public static class Json
    {
        public static object Read(string path) { return JsonStore.Serializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)); }
        public static object At(object value, params string[] keys)
        {
            foreach (var key in keys) { var map = value as Dictionary<string, object>; if (map == null || !map.TryGetValue(key, out value)) return null; }
            return value;
        }
        public static string Text(object value) { return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); }
        public static bool True(object value) { return string.Equals(Text(value), "true", StringComparison.OrdinalIgnoreCase); }
    }
    public static class Discovery
    {
        public static string Fingerprint(ScriptInstance i)
        {
            var files = new[] { i.Entry, Path.Combine(i.Directory, "MAA.dll"), Path.Combine(i.Directory, "MFAAvalonia.dll"), Path.Combine(i.Directory, "interface.json") };
            var text = new StringBuilder();
            using (var hash = SHA256.Create())
            {
                foreach (var path in files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                { using (var stream = File.OpenRead(path)) text.Append(Path.GetFileName(path)).Append(':').Append(Convert.ToBase64String(hash.ComputeHash(stream))).Append(';'); }
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
            }
        }
        public static ScriptInstance Detect(string directory)
        {
            string root = Path.GetFullPath(directory); string adapter = null, entry = null;
            bool protocol = File.Exists(Path.Combine(root, "interface.json"));
            if (File.Exists(Path.Combine(root, "MAA.exe")) && File.Exists(Path.Combine(root, "MaaCore.dll"))) { adapter = "MAA"; entry = "MAA.exe"; }
            else if (File.Exists(Path.Combine(root, "alas.py")) && Directory.Exists(Path.Combine(root, "module"))) { adapter = "Alas"; entry = "Alas.exe"; }
            else if (protocol)
            {
                foreach (var exe in Directory.GetFiles(root, "*.exe"))
                {
                    var meta = FileVersionInfo.GetVersionInfo(exe);
                    if (string.Equals(meta.ProductName, "MFAAvalonia", StringComparison.OrdinalIgnoreCase) ||
                        (Path.GetFileName(exe) == "MFAAvalonia.exe" && File.Exists(Path.Combine(root, "MFAAvalonia.dll"))))
                    { adapter = "MFAAvalonia"; entry = Path.GetFileName(exe); break; }
                }
                if (entry == null && File.Exists(Path.Combine(root, "MFW.exe")) && File.Exists(Path.Combine(root, "MFW_README.md"))) { adapter = "MFW"; entry = "MFW.exe"; }
                if (entry == null && File.Exists(Path.Combine(root, "MaaPiCli.exe"))) { adapter = "MaaPiCli"; entry = "MaaPiCli.exe"; }
            }
            else if (File.Exists(Path.Combine(root, "AALC.exe")) && File.Exists(Path.Combine(root, "config.yaml")) && Directory.Exists(Path.Combine(root, "_internal")))
            { adapter = "AALC"; entry = "AALC.exe"; }
            if (entry == null) return null;
            var i = new ScriptInstance { Directory = root, Entry = Path.Combine(root, entry), Adapter = adapter,
                Name = new DirectoryInfo(root).Name, ObserveOnly = adapter == "Alas", Enabled = false,
                Capability = "已发现 / 可启动入口；自动执行、结果识别与真实运行尚待核对" };
            if (File.Exists(i.Entry)) i.Version = FileVersionInfo.GetVersionInfo(i.Entry).FileVersion ?? "未声明";
            if (protocol) { var manifest = Json.Read(Path.Combine(root, "interface.json")); i.Version += " / 资源 " + Json.Text(Json.At(manifest, "version")); }
            if (adapter == "Alas") i.Capability = "只识别状态；不参与普通名额、不启动、不清理";
            if (adapter == "AALC") i.Capability = "已识别 GUI；此版本自动执行接口未核实，默认停用";
            PopulateBinding(i);
            i.Fingerprint = Fingerprint(i); return i;
        }
        public static void PopulateBinding(ScriptInstance i)
        {
            try
            {
                object adb = null;
                if (i.Adapter == "MAA")
                {
                    string file = Path.Combine(i.Directory, "config", "gui.new.json");
                    if (File.Exists(file))
                    {
                        var json = Json.Read(file); i.Profile = Json.Text(Json.At(json, "Current"));
                        var gui = Json.At(json, "Configurations", i.Profile, "Gui");
                        adb = Json.At(gui, "ConnectSettings", "Address");
                        var extras = Json.At(gui, "ConnectSettings", "Extras", "MuMuEmulator12");
                        int index;
                        string mumuRoot = Json.Text(Json.At(extras, "EmulatorPath"));
                        if (Json.True(Json.At(extras, "IsEnabled")) && int.TryParse(Json.Text(Json.At(extras, "InstanceIndex")), out index) && Directory.Exists(mumuRoot))
                        { i.MuMuManager = Path.Combine(mumuRoot, "shell", "MuMuManager.exe"); i.MuMuIndex = index; }
                        i.Arguments = new List<string> { "--config", i.Profile };
                        i.Capability = "配置接口已核对；需开启直接运行、配置完成后退出，并关闭全局 ADB 恢复";
                    }
                }
                else if (i.Adapter == "MFAAvalonia")
                {
                    string file = Path.Combine(i.Directory, "config", "instances", "default.json");
                    if (!File.Exists(file)) file = Path.Combine(i.Directory, "config", "config.json");
                    if (File.Exists(file)) adb = Json.At(Json.Read(file), "AdbDevice", "AdbSerial");
                    if (ModernMfa(i))
                    { i.Profile = "default"; i.Arguments = new List<string> { "--instance", i.Profile, "--autostart", "--quit-after-run" }; i.Capability = "已按本机程序集提交核对实例选择、自动执行及完成后退出参数；真实运行未验证"; }
                    else
                    { i.Profile = "Default"; i.Arguments = new List<string> { "-c", i.Profile }; i.Capability = "旧 MFA 配置选择已核对；需在原设置选择启动软件并执行任务、完成后关闭软件与模拟器"; }
                }
                else if (i.Adapter == "MFW")
                {
                    var config = Json.Read(Path.Combine(i.Directory, "config", "config.json"));
                    string resource = Json.Text(Json.At(config, "Maa", "Maa_resource_name"));
                    string profile = Json.Text(Json.At(config, "Maa", "Maa_config_name"));
                    if (resource.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
                    i.Profile = profile; i.Arguments = new List<string> { "-r", resource, "-c", profile, "-d" };
                    string file = Path.Combine(i.Directory, "config", resource, "config", profile, "maa_pi_config.json");
                    if (File.Exists(file)) adb = Json.At(Json.Read(file), "adb", "address");
                    i.Capability = "本地说明提供 -r/-c/-d；执行能力待用户核对，不能套用新版本参数";
                }
                string address = Json.Text(adb); int port;
                var pieces = address.Split(':');
                if (pieces.Length == 2 && int.TryParse(pieces[1], out port) && port > 0 && port < 65536)
                { i.ResourceHost = pieces[0]; i.ResourcePort = port; i.Target = "adb:" + address.ToLowerInvariant(); }
            }
            catch (Exception) { i.Capability += "；配置读取未完成，请手动核对"; }
        }
        public static bool ModernMfa(ScriptInstance i)
        {
            string dll = Path.Combine(i.Directory, "MFAAvalonia.dll");
            return File.Exists(dll) && (FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "").Contains("4f11c8122de4f43eafc818a368c9956e3b06249c");
        }
        public static List<ScriptInstance> Scan(IEnumerable<string> roots, CancellationToken cancel, Action<int, string> progress)
        {
            var result = new List<ScriptInstance>(); var stack = new Stack<Tuple<string, int>>();
            foreach (var root in roots) if (Directory.Exists(root)) stack.Push(Tuple.Create(Path.GetFullPath(root), 0));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skip = new HashSet<string>(new[] { "Windows", "node_modules", ".git", "$RECYCLE.BIN", "System Volume Information", "models", "assets", "resource", "cache", "log", "logs", "debug", "python", "venv", ".venv", "_internal", "VMware", "SteamLibrary" }, StringComparer.OrdinalIgnoreCase);
            int count = 0;
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested(); var current = stack.Pop();
                if (!seen.Add(current.Item1)) continue;
                try
                {
                    var directory = new DirectoryInfo(current.Item1);
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var instance = Detect(directory.FullName); count++;
                    if (progress != null && count % 20 == 0) progress(count, directory.Name);
                    if (instance != null) { result.Add(instance); continue; }
                    if (current.Item2 >= 5) continue;
                    foreach (var child in directory.EnumerateDirectories()) if (!skip.Contains(child.Name)) stack.Push(Tuple.Create(child.FullName, current.Item2 + 1));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (progress != null) progress(count, "扫描完成"); return result;
        }
    }
    public static class AdapterChecks
    {
        private static int? ShortcutIndex(string file)
        {
            if (!File.Exists(file) || !string.Equals(Path.GetExtension(file), ".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            object shell = null, shortcut = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell"); shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
                string target = Convert.ToString(shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
                string args = Convert.ToString(shortcut.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null));
                if (!string.Equals(Path.GetFileName(target), "MuMuPlayer.exe", StringComparison.OrdinalIgnoreCase)) return null;
                var match = Regex.Match(args, @"(?:^|\s)-v\s+(\d+)(?:\s|$)"); int index;
                return match.Success && int.TryParse(match.Groups[1].Value, out index) ? (int?)index : null;
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        public static string Validate(ScriptInstance i)
        {
            try
            {
                if (i.Adapter == "Alas" || i.Adapter == "OKWW") return "此适配器不允许自动执行";
                if (i.Adapter == "AALC") return "AALC 此版本仅核实发现和状态识别，自动执行接口未核实";
                if (i.Adapter == "MAA")
                {
                    var json = Json.Read(Path.Combine(i.Directory, "config", "gui.new.json"));
                    var gui = Json.At(json, "Configurations", i.Profile, "Gui");
                    if (gui == null || !Json.True(Json.At(gui, "StartUpSettings", "RunDirectly"))) return "MAA 尚未开启启动后直接运行";
                    if (Json.True(Json.At(gui, "ConnectSettings", "AlwaysAutoDetect")) || Json.True(Json.At(gui, "ConnectSettings", "AutoDetect"))) return "MAA 自动检测设备仍开启，请先固定并核实独立目标，避免串账号";
                    if (Json.True(Json.At(gui, "ConnectSettings", "AllowAdbHardRestart")) || Json.True(Json.At(gui, "ConnectSettings", "KillAdbOnExit")))
                        return "MAA 全局 ADB 强制恢复/退出关闭仍开启，请在原界面关闭";
                    string post = Json.Text(Json.At(gui, "PostActions"));
                    if (post.IndexOf("ExitSelf", StringComparison.OrdinalIgnoreCase) < 0)
                        return "MAA 需要核对完成后退出客户端设置；仅退出模拟器会留下脚本占位";
                    if (post.IndexOf("Shutdown", StringComparison.OrdinalIgnoreCase) >= 0 || post.IndexOf("Sleep", StringComparison.OrdinalIgnoreCase) >= 0 || post.IndexOf("Hibernate", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "MAA 完成动作包含关机/睡眠，请先调整以免打断其他任务";
                    if (!i.Arguments.SequenceEqual(new[] { "--config", i.Profile })) return "MAA 配置选择参数与本轮执行配置不一致";
                    if (Json.Text(Json.At(gui, "ConnectSettings", "Address")) != i.ResourceHost + ":" + i.ResourcePort) return "MAA 设备绑定已变化，请重新核对";
                    var extras = Json.At(gui, "ConnectSettings", "Extras", "MuMuEmulator12");
                    if (Json.True(Json.At(extras, "IsEnabled")))
                    {
                        string manager = Path.Combine(Json.Text(Json.At(extras, "EmulatorPath")), "shell", "MuMuManager.exe");
                        if (i.MuMuIndex.ToString() != Json.Text(Json.At(extras, "InstanceIndex")) || !string.Equals(manager, i.MuMuManager, StringComparison.OrdinalIgnoreCase)) return "MAA MuMu 增强绑定与管理器映射不一致";
                        string launcher = Json.Text(Json.At(gui, "StartUpSettings", "EmulatorPath"));
                        int? shortcutIndex = ShortcutIndex(launcher);
                        if (shortcutIndex.HasValue && shortcutIndex.Value != i.MuMuIndex) return "MAA 启动快捷方式和 MuMu 增强索引冲突，请核对以免串账号";
                        if (!Json.True(Json.At(gui, "StartUpSettings", "StartEmulator"))) return "MAA 尚未配置启动对应模拟器，无法无人值守执行";
                    }
                    var timers = Json.At(json, "Timers", "List") as IEnumerable;
                    if (timers != null) foreach (var timer in timers) if (Json.True(Json.At(timer, "IsEnabled"))) return "MAA 内置定时仍启用，请先避免重复触发";
                }
                if (i.Adapter == "MFAAvalonia")
                {
                    if (i.Profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "配置名称不合法";
                    if (i.Arguments.Any(a => a.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0)) return "禁止强制重启已有 MFA 任务";
                    if (Discovery.ModernMfa(i))
                    {
                        if (!File.Exists(Path.Combine(i.Directory, "config", "instances", i.Profile + ".json"))) return "MFA 实例配置不存在";
                        if (!i.Arguments.SequenceEqual(new[] { "--instance", i.Profile, "--autostart", "--quit-after-run" })) return "MFA 自动执行参数与所选实例不一致";
                    }
                    else
                    {
                        string version = FileVersionInfo.GetVersionInfo(i.Entry).FileVersion;
                        if (version != "1.8.7" && version != "2.2.7") return "MFA 客户端版本尚未核实，请重新核对适配器";
                        string path = Path.Combine(i.Directory, "config", i.Profile == "Default" ? "config.json" : "mfa_" + i.Profile + ".json");
                        var config = Json.Read(path); string before = Json.Text(Json.At(config, "BeforeTask"));
                        if (before != "StartupSoftwareAndScript" && !(version == "2.2.7" && before == "StartupScriptOnly")) return "MFA 尚未配置启动后执行任务（仅启动软件不够）";
                        string after = Json.Text(Json.At(config, "AfterTask"));
                        if (after != "CloseEmulatorAndMFA" && after != "CloseMFA") return "MFA 需要设置任务结束后退出客户端";
                        if (!i.Arguments.SequenceEqual(new[] { "-c", i.Profile })) return "旧 MFA 配置选择参数不一致";
                    }
                }
                if (i.Adapter == "MaaPiCli" && !File.Exists(Path.Combine(i.Directory, "config", "maa_pi_config.json")))
                    return "缺少 MaaPiCli 专属配置；不能复用 GUI 配置";
                if (i.Adapter == "MFW")
                {
                    if (i.Arguments.Count != 5 || i.Arguments[0] != "-r" || i.Arguments[2] != "-c" || i.Arguments[3] != i.Profile || i.Arguments[4] != "-d") return "MFW 资源/配置/直接运行参数未正确设置";
                    string resource = i.Arguments[1];
                    if (resource.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || i.Profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "MFW 配置名不合法";
                    if (!File.Exists(Path.Combine(i.Directory, "config", resource, "config", i.Profile, "maa_pi_config.json"))) return "MFW 所选独立配置不存在";
                    string readme = Path.Combine(i.Directory, "MFW_README.md");
                    if (!File.Exists(readme) || !File.ReadAllText(readme).Contains("MFW.exe -d")) return "MFW 本地参数说明不匹配，请重新核对版本";
                }
                return null;
            }
            catch { return "适配配置无法核对，停止启动"; }
        }
    }
}
