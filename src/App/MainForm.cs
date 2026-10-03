using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GameRelay.Core;
using GameRelay.Infrastructure;

namespace GameRelay.App
{
    public sealed class MainForm : Form
    {
        private readonly Scheduler engine;
        private readonly JsonStore store;
        private readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private CancellationTokenSource scanning;
        private Task worker;
        private State snapshot;
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly Label status = new Label { Dock = DockStyle.Top, Height = 65, Padding = new Padding(18, 14, 10, 0), Font = new Font("Microsoft YaHei UI", 12F) };
        private readonly Label footer = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(12, 6, 0, 0), Text = "准备核对已有实例…" };
        private readonly DataGridView running = Grid(), queue = Grid(), scripts = Grid(), schedules = Grid(), history = Grid();
        private readonly TextBox filter = new TextBox { Width = 240 };
        private readonly Button protection = new Button { AutoSize = true }, pause = new Button { AutoSize = true };
        private readonly NotifyIcon tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "GameRelay · 游序" };
        private readonly NumericUpDown normalLimit = new NumericUpDown { Minimum = 1, Maximum = 32, Width = 65 }, protectedLimit = new NumericUpDown { Minimum = 1, Maximum = 32, Width = 65 };
        private bool exiting, rendered;
        private readonly bool previewMode;
        protected override bool ShowWithoutActivation { get { return previewMode; } }
        private static DataGridView Grid()
        {
            return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                AutoGenerateColumns = true, RowTemplate = { Height = 32 }, ColumnHeadersHeight = 36 };
        }
        public MainForm(Scheduler engine, JsonStore store, bool preview = false, bool diagnostics = false)
        {
            this.engine = engine; this.store = store; snapshot = Clone(engine.Data);
            previewMode = preview || diagnostics;
            Text = "GameRelay · 游序"; Font = new Font("Microsoft YaHei UI", 9F); Size = new Size(1240, 780); MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(243, 246, 250);
            Controls.Add(tabs); Controls.Add(status); Controls.Add(footer);
            BuildHome(); BuildScripts(); BuildSchedules(); BuildHistory(); BuildSettings();
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开界面", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("暂停 / 恢复", null, delegate { Dispatch(delegate { engine.Data.Paused = !engine.Data.Paused; }); });
            menu.Items.Add("鸣潮保护切换", null, delegate { Dispatch(delegate { engine.Data.Protection = !engine.Data.Protection; }); });
            menu.Items.Add("彻底退出（保留已运行任务）", null, delegate { ExitManager(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); }; tray.Visible = !preview && !diagnostics;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else { engine.Stop(); shutdown.Cancel(); wake.Set(); } };
            Shown += delegate { if (!preview && worker == null) worker = Task.Run((Action)WorkLoop); };
            if (preview) Render(0, engine.Limit, new Dictionary<string, Observation>());
        }
        public void MeasureIdle(string directory, Func<double> probeCpu)
        {
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
            var timer = new System.Windows.Forms.Timer { Interval = 1000 }; int ticks = 0;
            var process = Process.GetCurrentProcess(); TimeSpan cpuStart = TimeSpan.Zero; double probeStart = 0; var watch = new Stopwatch(); long peak = 0;
            timer.Tick += delegate {
                ticks++; process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
                if (ticks == 3) { cpuStart = process.TotalProcessorTime; probeStart = probeCpu(); watch.Start(); }
                if (ticks >= 33)
                {
                    timer.Stop(); watch.Stop(); double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                    var report = new { SampleSeconds = watch.Elapsed.TotalSeconds, CpuMilliseconds = cpu,
                        ProbeCpuMilliseconds = probeCpu() - probeStart,
                        TotalCpuPercentOfMachine = (cpu + probeCpu() - probeStart) / watch.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount,
                        CpuPercentOfOneCore = cpu / watch.Elapsed.TotalMilliseconds * 100,
                        CpuPercentOfMachine = cpu / watch.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount,
                        WorkingSetMiB = process.WorkingSet64 / 1048576.0, PeakWorkingSetMiB = peak / 1048576.0,
                        PrivateMiB = process.PrivateMemorySize64 / 1048576.0, Instances = snapshot.Instances.Count,
                        ActiveSchedules = snapshot.Schedules.Count(s => s.Enabled), Observation = "Offscreen WinForms event loop; 5-second observation; no task launches" };
                    File.WriteAllText(Path.Combine(directory, "performance.json"), JsonStore.Serializer().Serialize(report));
                    timer.Dispose(); process.Dispose(); ExitManager();
                }
            };
            Shown += delegate { timer.Start(); };
        }
        public void RenderPreviews(string directory)
        {
            if (!previewMode) throw new InvalidOperationException();
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); Show();
            for (int index = 0; index < tabs.TabPages.Count; index++)
            {
                tabs.SelectedIndex = index; Application.DoEvents(); PerformLayout();
                using (var bitmap = new Bitmap(Width, Height))
                { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(directory, "preview-" + index + ".png")); }
            }
            Hide();
        }
        private static State Clone(State data) { return JsonStore.Serializer().Deserialize<State>(JsonStore.Serializer().Serialize(data)); }
        private TabPage Page(string name) { var page = new TabPage(name) { Padding = new Padding(12) }; tabs.TabPages.Add(page); return page; }
        private static FlowLayoutPanel Bar(Control parent)
        { var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 5, 0, 3), AutoScroll = true }; parent.Controls.Add(bar); return bar; }
        private static void Button(FlowLayoutPanel bar, string label, Action action)
        { var b = new Button { Text = label, AutoSize = true, Height = 30 }; b.Click += delegate { action(); }; bar.Controls.Add(b); }
        private static string[] Selection(DataGridView grid)
        { return grid.SelectedRows.Cast<DataGridViewRow>().Select(r => Convert.ToString(r.Cells["ID"].Value)).ToArray(); }
        private void BuildHome()
        {
            var page = Page("首页"); var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 250 };
            split.Panel1.Controls.Add(running); split.Panel1.Controls.Add(new Label { Text = "运行与资源占用 · 程序存在不代表任务正在执行", Dock = DockStyle.Top, Height = 32 });
            split.Panel2.Controls.Add(queue); split.Panel2.Controls.Add(new Label { Text = "等待队列 · 按原定触发时间与实例顺序补位", Dock = DockStyle.Top, Height = 32 }); page.Controls.Add(split);
            var bar = Bar(page); protection.Click += delegate { Dispatch(delegate { engine.Data.Protection = !engine.Data.Protection; }); }; pause.Click += delegate { Dispatch(delegate { engine.Data.Paused = !engine.Data.Paused; }); };
            bar.Controls.Add(protection); bar.Controls.Add(pause);
            Button(bar, "核对后解除选中阻塞", delegate { var ids = Selection(running); Dispatch(delegate { foreach (var id in ids) engine.Acknowledge(id, DateTime.UtcNow.Ticks); }); });
            Button(bar, "取消选中排队", delegate { var ids = Selection(queue); Dispatch(delegate { engine.CancelQueued(ids, DateTime.UtcNow.Ticks); }); });
            Button(bar, "彻底退出", ExitManager);
        }
        private void BuildScripts()
        {
            var page = Page("脚本实例"); page.Controls.Add(scripts); var bar = Bar(page);
            Button(bar, "扫描目录", Scan); Button(bar, "取消扫描", delegate { if (scanning != null) scanning.Cancel(); });
            Button(bar, "手动添加", delegate {
                using (var file = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe", Title = "选择原安装目录中的入口" })
                {
                    if (file.ShowDialog(this) != DialogResult.OK) return;
                    string dir = Path.GetDirectoryName(file.FileName); var found = Discovery.Detect(dir) ?? new ScriptInstance { Directory = dir, Entry = file.FileName, Adapter = "Custom", Capability = "自定义入口，需核对参数和资源" };
                    using (var edit = new InstanceEditor(found)) if (edit.ShowDialog(this) == DialogResult.OK) Dispatch(delegate { AddDiscovered(new[] { edit.Result }); });
                }
            });
            Button(bar, "编辑 / 修复路径", EditInstance);
            Button(bar, "重新读取适配", delegate {
                var ids = Selection(scripts); Dispatch(delegate {
                    foreach (var old in engine.Data.Instances.Where(i => ids.Contains(i.Id)).ToList())
                    {
                        if (engine.Data.Runs.Any(r => r.InstanceId == old.Id && (r.Active || r.Pending))) throw new InvalidOperationException("先取消排队并处理占用，再重新读取");
                        var fresh = Discovery.Detect(old.Directory); if (fresh == null) throw new InvalidOperationException("未能重新识别原目录");
                        fresh.Id = old.Id; fresh.Name = old.Name; fresh.Order = old.Order; fresh.TimeoutMinutes = old.TimeoutMinutes;
                        engine.Data.Instances[engine.Data.Instances.IndexOf(old)] = fresh;
                    }
                });
            });
            Button(bar, "启用 / 停用", delegate { var ids = Selection(scripts); Dispatch(delegate { foreach (var i in engine.Data.Instances.Where(i => ids.Contains(i.Id) && !i.ObserveOnly)) { if (i.Adapter == "AALC" || i.Adapter == "OKWW") throw new InvalidOperationException("该客户端只有发现与观察能力，不能启用自动执行"); i.Enabled = !i.Enabled; } }); });
            Button(bar, "立即运行（排队）", delegate { var ids = Selection(scripts); Dispatch(delegate { foreach (var id in ids) engine.Enqueue(id, DateTime.UtcNow.Ticks); }); });
            Button(bar, "批量计划", delegate { CreateSchedules(Selection(scripts)); });
            Button(bar, "查看记录", delegate { var ids = Selection(scripts); if (ids.Length > 0) { filter.Text = InstanceName(snapshot, ids[0]); tabs.SelectedIndex = 3; RenderHistory(); } });
            Button(bar, "打开文件夹", delegate { var ids = Selection(scripts); foreach (var i in snapshot.Instances.Where(i => ids.Contains(i.Id))) if (Directory.Exists(i.Directory)) Process.Start(new ProcessStartInfo(i.Directory) { UseShellExecute = true }); });
        }
        private void BuildSchedules()
        {
            var page = Page("计划"); page.Controls.Add(schedules); var bar = Bar(page);
            Button(bar, "为脚本页所选项添加", delegate { CreateSchedules(Selection(scripts)); });
            Button(bar, "编辑选中计划", delegate {
                var ids = Selection(schedules); var existing = snapshot.Schedules.FirstOrDefault(s => ids.Contains(s.Id)); if (existing == null) return;
                using (var editor = new ScheduleEditor(existing)) if (editor.ShowDialog(this) == DialogResult.OK)
                {
                    Dispatch(delegate { foreach (var s in engine.Data.Schedules.Where(s => ids.Contains(s.Id))) { ApplySchedule(s, editor); s.CursorUtc = Math.Max(s.CursorUtc, DateTime.UtcNow.Ticks); } });
                }
            });
            Button(bar, "启用 / 停用", delegate { var ids = Selection(schedules); Dispatch(delegate { foreach (var s in engine.Data.Schedules.Where(s => ids.Contains(s.Id))) { s.Enabled = !s.Enabled; if (s.Enabled) s.CursorUtc = Math.Max(s.CursorUtc, DateTime.UtcNow.Ticks); } }); });
            Button(bar, "删除计划", delegate { var ids = Selection(schedules); Dispatch(delegate { engine.Data.Schedules.RemoveAll(s => ids.Contains(s.Id)); }); });
        }
        private void BuildHistory()
        {
            var page = Page("运行记录"); page.Controls.Add(history); var bar = Bar(page);
            bar.Controls.Add(new Label { Text = "筛选名称 / 事件 / 原因", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); bar.Controls.Add(filter);
            filter.TextChanged += delegate { RenderHistory(); };
            Button(bar, "查看选中运行详情", delegate {
                var ids = Selection(history); var events = snapshot.Events.Where(e => ids.Contains(e.RunId)).ToList();
                if (events.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, events.Select(e => Local(e.Utc) + " · " + e.Kind + " · " + e.Message)), "本轮事件");
            });
        }
        private void BuildSettings()
        {
            var page = Page("设置与说明"); var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false }; page.Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "让游戏脚本按时启动、有序接力。", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 8, 4, 18) });
            layout.Controls.Add(new Label { Text = "正常普通任务上限", AutoSize = true }); normalLimit.Value = Math.Max(1, Math.Min(32, snapshot.NormalLimit)); layout.Controls.Add(normalLimit);
            layout.Controls.Add(new Label { Text = "鸣潮保护时普通任务上限", AutoSize = true }); protectedLimit.Value = Math.Max(1, Math.Min(32, snapshot.ProtectedLimit)); layout.Controls.Add(protectedLimit);
            var apply = new Button { Text = "保存并发设置", AutoSize = true }; layout.Controls.Add(apply); apply.Click += delegate { int n = (int)normalLimit.Value, p = (int)protectedLimit.Value; Dispatch(delegate { engine.Data.NormalLimit = n; engine.Data.ProtectedLimit = p; }); };
            var roots = new TextBox { Multiline = true, Width = 700, Height = 85, Text = string.Join(Environment.NewLine, snapshot.ScanRoots) }; layout.Controls.Add(new Label { Text = "扫描目录（每行一个，只在手动点击扫描时运行；最多五层）", AutoSize = true }); layout.Controls.Add(roots);
            var saveRoots = new Button { Text = "保存扫描目录", AutoSize = true }; layout.Controls.Add(saveRoots); saveRoots.Click += delegate { var values = roots.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).ToList(); Dispatch(delegate { engine.Data.ScanRoots = values; }); };
            var backup = new Button { Text = "备份管理器数据（包含本机配置，请勿公开）", AutoSize = true }; layout.Controls.Add(backup);
            backup.Click += delegate { using (var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "GameRelay-backup.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) { string path = dialog.FileName; Dispatch(delegate { store.Backup(path); }); } };
            var docs = new Button { Text = "打开使用说明", AutoSize = true }; layout.Controls.Add(docs); docs.Click += delegate { string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "USER_GUIDE.md"); if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); };
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Margin = new Padding(4, 20, 4, 4), Text =
                "窗口关闭后收起到托盘，仍继续调度。彻底退出只停止管理器，已运行任务继续存在。\nAlas 仅观察；鸣潮 / OKWW 由用户手动运行。鸣潮保护持久保存，不因进程探测而自行关闭。\n新发现项默认停用。程序退出只显示结果未确认，模拟测试不等于真实兼容验收。\n数据存放于便携目录 data，搬迁请连同该目录一起复制。外部安装路径失效时使用“编辑 / 修复路径”。\n日志保留最近 3000 条。不要直接公开 data、原脚本配置、真实日志或个人截图。\n当前时区：" + TimeZoneInfo.Local.Id + "（中国时区对应 Asia/Shanghai）" });
        }
        private void EditInstance()
        {
            var ids = Selection(scripts); var original = snapshot.Instances.FirstOrDefault(i => ids.Contains(i.Id)); if (original == null) return;
            using (var editor = new InstanceEditor(original)) if (editor.ShowDialog(this) == DialogResult.OK)
            {
                var result = editor.Result;
                Dispatch(delegate {
                    if (engine.Data.Runs.Any(r => r.InstanceId == result.Id && (r.Active || r.Pending))) throw new InvalidOperationException("该实例存在运行或排队，请先处理本轮再修改绑定");
                    int index = engine.Data.Instances.FindIndex(i => i.Id == result.Id); engine.Data.Instances[index] = result;
                });
            }
        }
        private static void ApplySchedule(Schedule s, ScheduleEditor e) { s.Minutes = e.Minutes; s.Weekdays = e.Days; s.TimeZoneId = e.Zone; s.Enabled = e.Active; }
        private void CreateSchedules(string[] ids)
        {
            if (ids.Length == 0) { MessageBox.Show(this, "先在脚本页选择一个或多个实例。", "批量计划"); return; }
            using (var edit = new ScheduleEditor(null)) if (edit.ShowDialog(this) == DialogResult.OK) Dispatch(delegate {
                foreach (var i in engine.Data.Instances.Where(i => ids.Contains(i.Id) && !i.ObserveOnly && i.Adapter != "AALC" && i.Adapter != "OKWW"))
                { var s = new Schedule { InstanceId = i.Id, Profile = i.Profile }; ApplySchedule(s, edit); engine.Data.Schedules.Add(s); }
            });
        }
        private async void Scan()
        {
            if (scanning != null) return; scanning = new CancellationTokenSource(); var token = scanning.Token; var roots = snapshot.ScanRoots.ToArray();
            try
            {
                var found = await Task.Run(() => Discovery.Scan(roots, token, (n, name) => Ui(delegate { footer.Text = "扫描 " + n + " 个目录 · " + name; })));
                Dispatch(delegate { AddDiscovered(found); }); footer.Text = "扫描发现 " + found.Count + " 个实例，新项均停用";
            }
            catch (OperationCanceledException) { footer.Text = "扫描已取消"; }
            catch (Exception error) { MessageBox.Show(this, error.Message, "扫描未完成"); }
            finally { scanning.Dispose(); scanning = null; }
        }
        private void AddDiscovered(IEnumerable<ScriptInstance> found)
        {
            foreach (var i in found) if (!engine.Data.Instances.Any(old => string.Equals(old.Directory, i.Directory, StringComparison.OrdinalIgnoreCase)))
            { i.Order = engine.Data.Instances.Count; i.Enabled = false; engine.Data.Instances.Add(i); }
        }
        private void Dispatch(Action action) { if (shutdown.IsCancellationRequested) return; commands.Enqueue(action); wake.Set(); }
        private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
        private void WorkLoop()
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    Action command; while (commands.TryDequeue(out command)) { try { command(); engine.Save(); } catch (Exception e) { string message = e.Message; Ui(delegate { MessageBox.Show(this, message, "操作未完成"); }); } }
                    engine.Tick(DateTime.UtcNow.Ticks);
                    foreach (var r in engine.Data.Runs.Where(r => !r.Notified && (r.State == "NeedsAttention" || (r.State == "Failed" && r.Attempt == 1))))
                    { r.Notified = true; string text = InstanceName(engine.Data, r.InstanceId) + "：" + r.Reason; Ui(delegate { tray.ShowBalloonTip(8000, "GameRelay 需要处理", text, ToolTipIcon.Warning); }); }
                    engine.Save(); var copy = Clone(engine.Data); int occupied = engine.Occupied, limit = engine.Limit;
                    var observations = engine.Observations.ToDictionary(x => x.Key, x => x.Value);
                    Ui(delegate { snapshot = copy; Render(occupied, limit, observations); });
                }
                catch (Exception error)
                {
                    engine.Stop(); string reason = error.GetType().Name;
                    Ui(delegate { footer.Text = "调度已停止：" + reason + "。保留 data 并查看排查说明。"; }); break;
                }
                wake.WaitOne(5000);
            }
        }
        private static string InstanceName(State s, string id) { var i = s.Instances.FirstOrDefault(x => x.Id == id); return i == null ? "未知实例" : i.Name; }
        private static string Local(long utc) { return utc <= 0 ? "—" : new DateTime(utc, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss"); }
        private static string Label(string state)
        {
            switch (state) { case "Starting": return "正在启动"; case "Running": return "管理器启动"; case "Residual": return "资源残留"; case "NeedsAttention": return "需要处理"; case "Queued": return "等待"; case "RetryQueued": return "等待一次重试"; case "ExternalCovered": return "外部覆盖"; case "ExitedUnknown": return "退出，结果未确认"; case "Failed": return "失败"; case "Succeeded": return "成功（有证据）"; default: return state; }
        }
        private static void Bind(DataGridView grid, object data)
        {
            string[] ids = grid.Columns.Contains("ID") ? Selection(grid) : new string[0]; int first = grid.FirstDisplayedScrollingRowIndex;
            grid.DataSource = data; if (grid.Columns.Contains("ID")) grid.Columns["ID"].Visible = false;
            grid.ClearSelection(); foreach (DataGridViewRow row in grid.Rows) if (ids.Contains(Convert.ToString(row.Cells["ID"].Value))) row.Selected = true;
            if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        }
        private void Render(int occupied, int limit, Dictionary<string, Observation> observations)
        {
            int alas = snapshot.Instances.Count(i => i.ObserveOnly && observations.ContainsKey(i.Id) && observations[i.Id].ScriptPresent);
            status.Text = "普通任务占用  " + occupied + " / " + limit + "     Alas 独立观察  " + alas + "     " + (snapshot.Paused ? "已暂停补位" : "调度中") + "\n本机时区 " + TimeZoneInfo.Local.Id;
            protection.Text = snapshot.Protection ? "鸣潮保护：开启（点击关闭）" : "鸣潮保护：关闭（点击开启）";
            protection.BackColor = snapshot.Protection ? Color.LightGoldenrodYellow : SystemColors.Control; pause.Text = snapshot.Paused ? "恢复补位" : "暂停补位";
            var rows = snapshot.Runs.Where(r => r.Active).Select(r => new { ID = r.Id, 名称 = InstanceName(snapshot, r.InstanceId), 来源 = r.Source, 状态 = Label(r.State), 开始 = Local(r.StartedUtc), 时长 = r.StartedUtc == 0 ? "—" : Math.Max(0, (DateTime.UtcNow.Ticks - r.StartedUtc) / TimeSpan.TicksPerMinute) + " 分钟", 原因 = r.Reason }).ToList();
            foreach (var i in snapshot.Instances.Where(i => !snapshot.Runs.Any(r => r.InstanceId == i.Id && r.Active)))
            {
                Observation o; if (observations.TryGetValue(i.Id, out o) && (o.ScriptPresent || o.ResourceBusy || o.Uncertain))
                    rows.Add(new { ID = "external:" + i.Id, 名称 = i.Name, 来源 = i.ObserveOnly ? "独立观察" : "外部", 状态 = "未确认执行", 开始 = "未知", 时长 = "未知", 原因 = o.Detail });
            }
            Bind(running, rows); Bind(queue, snapshot.Runs.Where(r => r.Pending).OrderBy(r => r.DueUtc).Select(r => new { ID = r.Id, 名称 = InstanceName(snapshot, r.InstanceId), 来源 = r.Source, 原定时间 = Local(r.DueUtc), 重试次数 = r.Attempt, 等待原因 = r.Reason }).ToList());
            Bind(scripts, snapshot.Instances.Select(i => new { ID = i.Id, 名称 = i.Name, 客户端 = i.Adapter, 启用 = i.Enabled ? "是" : "否", 版本 = i.Version, 路径 = i.Directory, 入口 = Path.GetFileName(i.Entry), 目标 = i.Target, 自动执行 = i.AutoRunVerified ? "已核对" : "待核对", 真实验证 = i.RealRunVerified ? "通过" : "未验证", 兼容说明 = i.Capability }).ToList());
            Bind(schedules, snapshot.Schedules.Select(s => new { ID = s.Id, 实例 = InstanceName(snapshot, s.InstanceId), 配置 = s.Profile, 启用 = s.Enabled ? "是" : "否", 时间 = string.Join(",", s.Minutes.Select(m => (m / 60).ToString("00") + ":" + (m % 60).ToString("00"))), 星期 = string.Join(",", s.Weekdays.Select(d => d == 0 ? "日" : d.ToString())), 时区 = s.TimeZoneId, 下次 = Next(s), 补跑 = snapshot.Runs.Any(r => r.ScheduleId == s.Id && r.Pending && r.Source == "合并补跑") ? "等待补跑" : "无" }).ToList());
            RenderHistory(); if (!rendered) { footer.Text = "已有实例核对完成 · 新发现项默认停用 · 关闭窗口后继续在托盘运行"; rendered = true; }
        }
        private static string Next(Schedule s) { try { return s.Enabled ? Local(Planner.Next(s, Math.Max(DateTime.UtcNow.Ticks, s.CursorUtc)) ?? 0) : "已停用"; } catch { return "时区或计划需修复"; } }
        private void RenderHistory()
        {
            string query = filter.Text.Trim();
            Bind(history, snapshot.Events.AsEnumerable().Reverse().Where(e => (InstanceName(snapshot, e.InstanceId) + e.Kind + e.Message).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(1000).Select(e => new { ID = e.RunId, 时间 = Local(e.Utc), 实例 = InstanceName(snapshot, e.InstanceId), 事件 = e.Kind, 原定时间 = Local(snapshot.Runs.Where(r => r.Id == e.RunId).Select(r => r.DueUtc).FirstOrDefault()), 说明 = e.Message }).ToList());
        }
        private async void ExitManager()
        {
            if (exiting) return; exiting = true; engine.Stop(); shutdown.Cancel(); wake.Set(); if (scanning != null) scanning.Cancel();
            Enabled = false; footer.Text = "正在停止调度，已运行任务将继续…";
            if (worker != null) await worker;
            tray.Visible = false; Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { engine.Stop(); shutdown.Cancel(); wake.Set(); tray.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
