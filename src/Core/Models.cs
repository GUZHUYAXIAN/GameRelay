using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRelay.Core
{
    public sealed class ScriptInstance
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Directory { get; set; }
        public string Entry { get; set; }
        public string Adapter { get; set; }
        public string Version { get; set; }
        public string Fingerprint { get; set; }
        public string Profile { get; set; }
        public List<string> Arguments { get; set; }
        public string Target { get; set; }
        public string ResourceHost { get; set; }
        public int ResourcePort { get; set; }
        public string ResourceProcess { get; set; }
        public string MuMuManager { get; set; }
        public int MuMuIndex { get; set; }
        public bool ResourceVerified { get; set; }
        public bool Enabled { get; set; }
        public bool ObserveOnly { get; set; }
        public bool AutoRunVerified { get; set; }
        public bool RealRunVerified { get; set; }
        public int TimeoutMinutes { get; set; }
        public int Order { get; set; }
        public string Capability { get; set; }
        public ScriptInstance()
        {
            Id = Guid.NewGuid().ToString("N"); Name = "新实例"; Profile = "default";
            Arguments = new List<string>(); TimeoutMinutes = 120; Target = ""; MuMuIndex = -1;
        }
    }
    public sealed class Schedule
    {
        public string Id { get; set; }
        public string InstanceId { get; set; }
        public string Profile { get; set; }
        public List<int> Minutes { get; set; }
        public List<int> Weekdays { get; set; }
        public string TimeZoneId { get; set; }
        public long CreatedUtc { get; set; }
        public long CursorUtc { get; set; }
        public bool Enabled { get; set; }
        public Schedule()
        {
            Id = Guid.NewGuid().ToString("N"); Profile = "default";
            Minutes = new List<int>(); Weekdays = new List<int>();
            TimeZoneId = TimeZoneInfo.Local.Id; CreatedUtc = CursorUtc = DateTime.UtcNow.Ticks;
        }
    }
    public sealed class Run
    {
        public string Id { get; set; }
        public string InstanceId { get; set; }
        public string ScheduleId { get; set; }
        public string Profile { get; set; }
        public string Source { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public long DueUtc { get; set; }
        public long StartedUtc { get; set; }
        public long EndedUtc { get; set; }
        public int Attempt { get; set; }
        public int Pid { get; set; }
        public long ProcessStartUtc { get; set; }
        public string ProcessPath { get; set; }
        public string Session { get; set; }
        public string Target { get; set; }
        public bool Notified { get; set; }
        public bool Active { get { return State == "Starting" || State == "Running" || State == "Cleaning" || State == "Residual" || State == "NeedsAttention"; } }
        public bool Pending { get { return State == "Queued" || State == "RetryQueued"; } }
    }
    public sealed class EventRecord
    {
        public long Utc { get; set; }
        public string InstanceId { get; set; }
        public string RunId { get; set; }
        public string Kind { get; set; }
        public string Message { get; set; }
    }
    public sealed class State
    {
        public int Schema { get; set; }
        public int NormalLimit { get; set; }
        public int ProtectedLimit { get; set; }
        public bool Protection { get; set; }
        public bool Paused { get; set; }
        public int Retention { get; set; }
        public List<string> ScanRoots { get; set; }
        public List<ScriptInstance> Instances { get; set; }
        public List<Schedule> Schedules { get; set; }
        public List<Run> Runs { get; set; }
        public List<EventRecord> Events { get; set; }
        public State()
        {
            Schema = 1; NormalLimit = 3; ProtectedLimit = 1; Retention = 3000;
            ScanRoots = new List<string> { @"E:\" }; Instances = new List<ScriptInstance>();
            Schedules = new List<Schedule>(); Runs = new List<Run>(); Events = new List<EventRecord>();
        }
        public void Record(long now, string kind, Run run, string message)
        {
            Events.Add(new EventRecord { Utc = now, Kind = kind, RunId = run == null ? "" : run.Id,
                InstanceId = run == null ? "" : run.InstanceId, Message = message });
            int keep = Math.Max(100, Math.Min(10000, Retention));
            if (Events.Count > keep) Events.RemoveRange(0, Events.Count - keep);
            var completed = Runs.Where(r => !r.Active && !r.Pending).OrderByDescending(r => r.DueUtc).Skip(keep).ToList();
            foreach (var old in completed) Runs.Remove(old);
        }
    }
    public sealed class Observation
    {
        public bool ScriptPresent;
        public bool ResourceBusy;
        public bool Uncertain;
        public bool OwnedAlive;
        public bool Failed;
        public bool SuccessEvidence;
        public string Detail = "";
    }
    public sealed class LaunchResult
    {
        public int Pid;
        public long StartUtc;
        public string Path;
    }
    public interface IRuntime
    {
        Dictionary<string, Observation> Observe(IList<ScriptInstance> instances, IList<Run> runs);
        string Validate(ScriptInstance instance);
        LaunchResult Start(ScriptInstance instance, Run run);
        bool Cleanup(ScriptInstance instance, Run run);
    }
    public interface IStateStore { void Save(State state); }
}
