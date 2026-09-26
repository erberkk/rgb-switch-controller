using System;
using System.Collections.Generic;
using System.IO;

namespace PcControl.Core
{
    public sealed class SavedState
    {
        public bool LightsOff;
        public DateTime? ChangedAt;
        public Dictionary<string, Dictionary<string, object>> Snapshots = new Dictionary<string, Dictionary<string, object>>();
    }

    public static class StateStore
    {
        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcControl");

        static readonly string FilePath = Path.Combine(Folder, "state.json");

        public static SavedState Load()
        {
            var state = new SavedState();
            var legacy = Path.Combine(Path.GetDirectoryName(Folder), "RgbSwitch", "state.json");
            if (!File.Exists(FilePath) && File.Exists(legacy))
            {
                Directory.CreateDirectory(Folder);
                File.Copy(legacy, FilePath);
            }
            if (!File.Exists(FilePath)) return state;
            var root = Json.ReadObject(File.ReadAllText(FilePath));
            state.LightsOff = root.Bool("lightsOff") ?? false;
            if (DateTime.TryParse(root.Str("changedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                state.ChangedAt = at;
            if (Json.Obj(root.Get("snapshots")) is Dictionary<string, object> snaps)
                foreach (var kv in snaps)
                    if (Json.Obj(kv.Value) is Dictionary<string, object> snap)
                        state.Snapshots[kv.Key] = snap;
            return state;
        }

        public static void Save(SavedState state)
        {
            Directory.CreateDirectory(Folder);
            var root = new Dictionary<string, object>
            {
                ["lightsOff"] = state.LightsOff,
                ["changedAt"] = state.ChangedAt?.ToString("o"),
                ["snapshots"] = state.Snapshots,
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Write(root));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }
}
