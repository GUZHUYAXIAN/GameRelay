using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRelay.Core
{
    public sealed class Scheduler
    {
        public readonly State Data;
        public readonly string Session = Guid.NewGuid().ToString("N");
        private readonly IRuntime runtime;
        private readonly IStateStore store;
        public Dictionary<string, Observation> Observations { get; private set; }
        private volatile bool stopped;
        private readonly object launchGate = new object();
        public bool Stopped { get { return stopped; } }
        public int Limit { get { return Math.Max(1, Data.Protection ? Data.ProtectedLimit : Data.NormalLimit); } }
        public Scheduler(State data, IRuntime runtime, IStateStore store)
        {
            Data = data; this.runtime = runtime; this.store = store;
            Observations = new Dictionary<string, Observation>();
            // Ownership never transfers merely because a PID is still present.
            foreach (var run in Data.Runs.Where(r => r.Active && r.State != "NeedsAttention").ToList())
            {
                run.State = "NeedsAttention"; run.Reason = "管理器已重启：需核对外部运行与残留资源，自动恢复已停止";
                Data.Record(DateTime.UtcNow.Ticks, "恢复核对", run, run.Reason);
            }
            store.Save(Data);
        }
        public int Occupied
        {
            get
            {
                return Data.Instances.Count(i => !i.ObserveOnly &&
                    ((Observations.ContainsKey(i.Id) && (Observations[i.Id].ScriptPresent || Observations[i.Id].ResourceBusy || Observations[i.Id].Uncertain)) ||
                    Data.Runs.Any(r => r.InstanceId == i.Id && r.Active)));
            }
        }
        public void Stop() { lock (launchGate) stopped = true; }
        public void Save() { store.Save(Data); }
        public void Enqueue(string instanceId, long now)
        {
            var i = Data.Instances.Single(x => x.Id == instanceId);
            if (i.ObserveOnly) throw new InvalidOperationException("此实例仅观察，不接受启动请求");
            if (i.Adapter == "AALC" || i.Adapter == "OKWW") throw new InvalidOperationException("此客户端当前仅支持发现与观察，没有已核实的自动执行接口");
            Queue(i, "", i.Profile, "立即运行", now, now); store.Save(Data);
        }
        private void Queue(ScriptInstance i, string scheduleId, string profile, string source, long due, long now)
        {
            // Different schedules retain separate debt; manual requests merge with the same execution profile.
            var existing = Data.Runs.FirstOrDefault(r => r.InstanceId == i.Id && r.Profile == profile && (r.Pending || r.Active) &&
                (r.ScheduleId == scheduleId || scheduleId == "" || r.ScheduleId == ""));
            if (existing != null)
            {
                existing.DueUtc = Math.Min(existing.DueUtc, due);
                Data.Record(now, "合并", existing, source + "与本轮相同执行配置合并"); return;
            }
            var run = new Run { Id = Guid.NewGuid().ToString("N"), InstanceId = i.Id, ScheduleId = scheduleId,
                Profile = profile, Source = source, DueUtc = due, State = "Queued", Reason = "等待名额", Target = i.Target };
            Observation o;
            if (Observations.TryGetValue(i.Id, out o) && o.ScriptPresent && !Data.Runs.Any(r => r.InstanceId == i.Id && r.Active && r.Session == Session))
            { run.State = "ExternalCovered"; run.EndedUtc = now; run.Reason = "由外部运行覆盖；执行结果未确认"; }
            Data.Runs.Add(run); Data.Record(now, run.State == "ExternalCovered" ? "外部覆盖" : "排队", run, run.Reason);
        }
        public void Tick(long now)
        {
            if (Stopped) return;
            Observations = runtime.Observe(Data.Instances, Data.Runs);
            foreach (var schedule in Data.Schedules.Where(s => s.Enabled))
            {
                var i = Data.Instances.FirstOrDefault(x => x.Id == schedule.InstanceId);
                if (i == null || i.ObserveOnly) continue;
                long? due = Planner.Next(schedule, Math.Max(schedule.CreatedUtc, schedule.CursorUtc));
                if (due.HasValue && due.Value <= now)
                {
                    Queue(i, schedule.Id, schedule.Profile, now - due.Value > TimeSpan.FromMinutes(1).Ticks ? "合并补跑" : "计划", due.Value, now);
                    schedule.CursorUtc = Math.Max(schedule.CursorUtc, now);
                }
            }
            foreach (var run in Data.Runs.Where(r => r.State == "Running" || r.State == "Residual").ToList())
            {
                var i = Data.Instances.Single(x => x.Id == run.InstanceId);
                var o = Observations[i.Id];
                if (run.Session != Session) { Attention(run, now, "归属无法确认，停止自动恢复"); continue; }
                bool timeout = i.TimeoutMinutes > 0 && now - run.StartedUtc >= TimeSpan.FromMinutes(i.TimeoutMinutes).Ticks;
                if (timeout || o.Failed)
                {
                    if (i.AutomaticRecoveryDisabled) { Attention(run, now, "自动恢复已关闭：超时或异常仅提示，未清理、未重试"); continue; }
                    run.State = "Cleaning"; run.Reason = timeout ? "超过最长运行时间" : "进程明确异常退出";
                    Data.Record(now, "清理", run, run.Reason); store.Save(Data);
                    if (!runtime.Cleanup(i, run)) { Attention(run, now, "清理失败或资源残留；未启动重试"); continue; }
                    Observations = runtime.Observe(Data.Instances, Data.Runs);
                    o = Observations[i.Id];
                    if (o.ScriptPresent || o.ResourceBusy || o.Uncertain) { Attention(run, now, "清理后仍有占用或归属不明"); continue; }
                    if (run.Attempt >= 1) { run.State = "Failed"; run.EndedUtc = now; run.Reason = "重试失败，本轮停止"; Data.Record(now, "重试失败", run, run.Reason); }
                    else { run.Attempt++; run.State = "RetryQueued"; run.Reason = "已确认清理，等待一次重试"; Data.Record(now, "重试排队", run, run.Reason); }
                }
                else if (!o.OwnedAlive)
                {
                    if (o.ResourceBusy || o.ScriptPresent || o.Uncertain) { run.State = "Residual"; run.Reason = "脚本退出，资源仍占用或待核对"; }
                    else { run.State = o.SuccessEvidence ? "Succeeded" : "ExitedUnknown"; run.EndedUtc = now; run.Reason = o.SuccessEvidence ? "有可靠完成证据" : "已退出，结果未确认"; Data.Record(now, "退出", run, run.Reason); }
                }
            }
            store.Save(Data); // Commit occurrences and transitions before any launch.
            foreach (var run in Data.Runs.Where(r => r.Pending).OrderBy(r => r.DueUtc)
                .ThenBy(r => Data.Instances.Single(i => i.Id == r.InstanceId).Order).ThenBy(r => r.Id).ToList())
            {
                if (Stopped) break;
                var i = Data.Instances.Single(x => x.Id == run.InstanceId);
                var o = Observations[i.Id];
                bool own = Data.Runs.Any(r => r.InstanceId == i.Id && r.Active && r.Session == Session);
                if (o.ScriptPresent && !own)
                { run.State = "ExternalCovered"; run.EndedUtc = now; run.Reason = "由外部运行覆盖；执行结果未确认"; Data.Record(now, "外部覆盖", run, run.Reason); continue; }
                if (Data.Paused) { run.Reason = "调度已暂停"; continue; }
                if (!i.Enabled) { run.Reason = "实例未启用"; continue; }
                if (Data.Runs.Any(r => r.InstanceId == i.Id && r.Active)) { run.Reason = "同一实例正在运行或需要处理"; continue; }
                if (run.Profile != i.Profile) { run.Reason = "执行配置已变化，请核对计划"; continue; }
                if (o.Uncertain || o.ResourceBusy) { run.Reason = "资源残留或探测不确定"; continue; }
                if (Occupied >= Limit) { run.Reason = "等待普通任务名额"; continue; }
                if (Data.Instances.Any(other => other.Id != i.Id && !string.IsNullOrEmpty(i.Target) && other.Target == i.Target &&
                    (Data.Runs.Any(r => r.InstanceId == other.Id && r.Active) ||
                    (Observations.ContainsKey(other.Id) && (Observations[other.Id].ScriptPresent || Observations[other.Id].ResourceBusy || Observations[other.Id].Uncertain)))))
                { run.Reason = "独占目标正被其他实例占用"; continue; }
                string invalid = runtime.Validate(i);
                if (invalid != null) { run.Reason = invalid; continue; }
                run.State = "Starting"; run.Session = Session; run.StartedUtc = now; run.Reason = "启动意图已落盘";
                store.Save(Data);
                try
                {
                    LaunchResult started;
                    lock (launchGate)
                    {
                        if (Stopped) { run.State = run.Attempt > 0 ? "RetryQueued" : "Queued"; run.Reason = "管理器已退出，未启动"; store.Save(Data); break; }
                        started = runtime.Start(i, run);
                    }
                    run.Pid = started.Pid; run.ProcessStartUtc = started.StartUtc; run.ProcessPath = started.Path;
                    run.State = "Running"; run.Reason = "管理器启动；结果待确认";
                    Data.Record(now, "启动", run, run.Attempt == 0 ? "首次启动" : "第 1 次且最后一次重试");
                }
                catch (Exception) { Attention(run, now, "启动未确认；为防重复请核对进程与资源"); }
                store.Save(Data); // A persistence failure must escape and stop the entire scheduler.
                Observations = runtime.Observe(Data.Instances, Data.Runs);
            }
            store.Save(Data);
        }
        private void Attention(Run run, long now, string reason)
        { run.State = "NeedsAttention"; run.Reason = reason; Data.Record(now, "需要处理", run, reason); }
        public void Acknowledge(string runId, long now)
        {
            Observations = runtime.Observe(Data.Instances, Data.Runs);
            var run = Data.Runs.Single(r => r.Id == runId); var o = Observations[run.InstanceId];
            if (run.State != "NeedsAttention" || o.ScriptPresent || o.ResourceBusy || o.Uncertain)
                throw new InvalidOperationException("仍有进程/资源占用或状态不确定，不能释放");
            run.State = "ExitedUnknown"; run.EndedUtc = now; run.Reason = "人工核对后解除阻塞，结果未确认";
            Data.Record(now, "人工核对", run, run.Reason); store.Save(Data);
        }
        public void CancelQueued(IEnumerable<string> ids, long now)
        {
            foreach (var run in Data.Runs.Where(r => ids.Contains(r.Id) && r.Pending).ToList())
            { run.State = "Cancelled"; run.EndedUtc = now; run.Reason = "用户取消本轮排队；以后计划不受影响"; Data.Record(now, "取消排队", run, run.Reason); }
            store.Save(Data);
        }
    }
}
