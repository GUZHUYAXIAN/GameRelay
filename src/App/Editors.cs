using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GameRelay.Core;
using GameRelay.Infrastructure;

namespace GameRelay.App
{
    public class Editor : Form
    {
        protected readonly TableLayoutPanel Fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
        protected readonly Button SaveButton = new Button { Text = "保存", AutoSize = true };
        public Editor(string title)
        {
            Text = title; Font = new Font("Microsoft YaHei UI", 9F); Size = new Size(770, 680); MinimumSize = new Size(650, 480);
            StartPosition = FormStartPosition.CenterParent; AutoScroll = true; BackColor = Color.White;
            Fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); Fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(Fields);
        }
        protected TextBox Field(string label, string value, bool multiline)
        {
            var box = new TextBox { Text = value ?? "", Dock = DockStyle.Fill, Multiline = multiline, Height = multiline ? 75 : 28, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
            Add(label, box); return box;
        }
        protected void Add(string label, Control control)
        {
            int row = Fields.RowCount++; Fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 8) }, 0, row);
            control.Margin = new Padding(0, 4, 0, 5); Fields.Controls.Add(control, 1, row);
        }
        protected void Finish(Action save)
        {
            Add("", SaveButton); SaveButton.Click += delegate { try { save(); DialogResult = DialogResult.OK; Close(); } catch (Exception error) { MessageBox.Show(this, error.Message, "请检查填写内容"); } };
        }
    }
    public sealed class InstanceEditor : Editor
    {
        public ScriptInstance Result;
        public InstanceEditor(ScriptInstance input) : base("编辑实例 · 不修改原脚本配置")
        {
            Result = JsonStore.Serializer().Deserialize<ScriptInstance>(JsonStore.Serializer().Serialize(input));
            Field("实例 ID（只读）", input.Id, false).ReadOnly = true;
            var name = Field("显示名称", input.Name, false); var dir = Field("安装目录", input.Directory, false);
            var entry = Field("EXE 完整路径", input.Entry, false); var profile = Field("执行配置", input.Profile, false);
            var args = Field("参数（每行一个）", string.Join(Environment.NewLine, input.Arguments), true);
            var target = Field("独占目标标识", input.Target, false);
            var host = Field("资源地址（本机）", input.ResourceHost, false); var port = Field("资源监听端口（0 无）", input.ResourcePort.ToString(), false);
            var resourceExe = Field("游戏/资源 EXE 路径", input.ResourceProcess, false);
            var mumu = Field("MuMuManager.exe（可选）", input.MuMuManager, false); var mumuIndex = Field("MuMu 索引（-1 未绑定）", input.MuMuIndex.ToString(), false);
            var timeout = Field("超时分钟（0 关闭）", input.TimeoutMinutes.ToString(), false); var order = Field("同时间排序（小者优先）", input.Order.ToString(), false);
            var recovery = new CheckBox { Text = "允许归属核实后的自动清理与一次重试（首轮验收可关闭）", AutoSize = true, Checked = !input.AutomaticRecoveryDisabled }; Add("异常恢复", recovery);
            var auto = new CheckBox { Text = "我已核对：启动后会自动执行所选配置", AutoSize = true, Checked = input.AutoRunVerified }; Add("自动执行核对", auto);
            var resource = new CheckBox { Text = "我已核对目标映射及残留资源探测", AutoSize = true, Checked = input.ResourceVerified }; Add("资源核对", resource);
            Add("边界", new Label { Text = "修改目录、入口或参数后需重新核对。仅进程型自定义任务可使用 process-only:<实例ID>。\n能力核对不等于真实运行验收通过。不要在参数中填写密码或令牌。", AutoSize = true, MaximumSize = new Size(520, 0) });
            Finish(delegate {
                int minutes, rank, resourcePort, index;
                if (!int.TryParse(timeout.Text, out minutes) || minutes < 0 || minutes > 10080 || !int.TryParse(order.Text, out rank) ||
                    !int.TryParse(port.Text, out resourcePort) || resourcePort < 0 || resourcePort > 65535 || !int.TryParse(mumuIndex.Text, out index) || index < -1) throw new ArgumentException("超时、排序、端口或模拟器索引格式不正确");
                if (string.IsNullOrWhiteSpace(name.Text) || !Path.IsPathRooted(dir.Text) || !Path.IsPathRooted(entry.Text)) throw new ArgumentException("请填写名称和完整安装/入口路径");
                Result.Name = name.Text.Trim(); Result.Directory = Path.GetFullPath(dir.Text); Result.Entry = Path.GetFullPath(entry.Text);
                Result.Profile = profile.Text.Trim(); Result.Arguments = args.Lines.Where(x => x.Length > 0).ToList();
                Result.Target = target.Text.Trim(); Result.ResourceHost = host.Text.Trim(); Result.ResourcePort = resourcePort; Result.ResourceProcess = resourceExe.Text.Trim();
                Result.MuMuManager = mumu.Text.Trim(); Result.MuMuIndex = index;
                Result.TimeoutMinutes = minutes; Result.Order = rank; Result.AutoRunVerified = auto.Checked; Result.ResourceVerified = resource.Checked;
                Result.AutomaticRecoveryDisabled = !recovery.Checked;
                Result.Fingerprint = Discovery.Fingerprint(Result); Result.RealRunVerified = false;
                if (Result.ObserveOnly) { Result.Enabled = false; Result.AutoRunVerified = false; }
            });
        }
    }
    public sealed class ScheduleEditor : Editor
    {
        public List<int> Minutes; public List<int> Days; public string Zone; public bool Active;
        public ScheduleEditor(Schedule original) : base("编辑计划 · 可批量用于所选实例")
        {
            Height = 470;
            var times = Field("每天时间（逗号分隔）", original == null ? "" : string.Join(", ", original.Minutes.Select(m => (m / 60).ToString("00") + ":" + (m % 60).ToString("00"))), false);
            var weekdays = new CheckedListBox { CheckOnClick = true, Height = 156, Dock = DockStyle.Fill };
            string[] labels = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
            for (int n = 0; n < 7; n++) weekdays.Items.Add(labels[n], original == null || original.Weekdays.Contains(n));
            Add("指定星期", weekdays);
            var zone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (var z in TimeZoneInfo.GetSystemTimeZones()) zone.Items.Add(z.Id);
            zone.SelectedItem = original == null ? TimeZoneInfo.Local.Id : original.TimeZoneId; Add("时区（中国 = China Standard Time）", zone);
            var enabled = new CheckBox { Text = "启用此计划", AutoSize = true, Checked = original != null && original.Enabled }; Add("计划状态", enabled);
            Finish(delegate {
                var parsed = new List<int>();
                foreach (string part in times.Text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    TimeSpan value; if (!TimeSpan.TryParseExact(part.Trim(), new[] { @"h\:mm", @"hh\:mm" }, System.Globalization.CultureInfo.InvariantCulture, out value) || value.TotalHours >= 24) throw new ArgumentException("请输入 HH:mm 时间，例如 08:30；示例不会自动保存");
                    parsed.Add((int)value.TotalMinutes);
                }
                if (parsed.Count == 0 || weekdays.CheckedIndices.Count == 0 || zone.SelectedItem == null) throw new ArgumentException("至少选择一个时间、星期和时区");
                Minutes = parsed.Distinct().OrderBy(x => x).ToList(); Days = weekdays.CheckedIndices.Cast<int>().ToList(); Zone = zone.SelectedItem.ToString(); Active = enabled.Checked;
            });
        }
    }
}
