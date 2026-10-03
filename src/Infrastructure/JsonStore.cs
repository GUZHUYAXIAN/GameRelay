using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using GameRelay.Core;

namespace GameRelay.Infrastructure
{
    public sealed class JsonStore : IStateStore
    {
        public readonly string Path;
        private string lastSaved;
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 100 }; }
        public JsonStore(string directory) { Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "state.json"); }
        public State Load()
        {
            if (!File.Exists(Path))
            {
                if (File.Exists(Path + ".bak") || File.Exists(Path + ".tmp")) throw new InvalidDataException("主状态文件缺失，存在恢复文件；请先恢复数据，不会创建空计划状态");
                return new State();
            }
            // Never silently fall back to an older cursor after corruption: that can double-launch.
            var text = File.ReadAllText(Path, Encoding.UTF8);
            var state = Serializer().Deserialize<State>(text);
            if (state == null || state.Schema != 1) throw new InvalidDataException("不支持的数据版本；保留原文件，请使用对应版本恢复");
            if (state.Instances == null || state.Runs == null || state.Schedules == null || state.Events == null ||
                state.Instances.Select(i => i.Id).Distinct().Count() != state.Instances.Count ||
                state.Runs.Any(r => !state.Instances.Any(i => i.Id == r.InstanceId)))
                throw new InvalidDataException("数据关系损坏；已阻止调度");
            lastSaved = text; return state;
        }
        public void Save(State state)
        {
            var text = Serializer().Serialize(state);
            if (text == lastSaved) return;
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var file = new FileStream(Path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(Path)) File.Replace(Path + ".tmp", Path, Path + ".bak", true);
            else File.Move(Path + ".tmp", Path);
            lastSaved = text;
        }
        public void Backup(string destination) { File.Copy(Path, destination, false); }
    }
}
