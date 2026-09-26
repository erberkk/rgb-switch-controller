using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PcControl.Core;

namespace PcControl.Controllers
{
    // Drives Lian Li L-Connect 3 through the same local service calls its own UI makes
    // (POST http://127.0.0.1:11021/?action=Device&type=...). The request bodies are the
    // ones the UI last sent, which L-Connect keeps in its "LWireless-UnBindDevice-Setting" file.
    public sealed class LConnectController : IDeviceController
    {
        const string ServiceUrl = "http://127.0.0.1:11021/";
        const int WirelessTxType = 16973824;
        const int WirelessLcdType = 16982016;
        const int ScopeAll = 5;

        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "L-Connect 3");

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public string Id => "lconnect";
        public string Title => "Fanlar ve Strimer";
        public string Source => "L-Connect 3";
        public string Glyph => "";
        public bool IsInstalled => Directory.Exists(Path.Combine(DataDir, "device"));

        sealed class Plan
        {
            public string TxPath;
            public string LcdPath;
            public List<(string type, Dictionary<string, object> body)> Lighting = new List<(string, Dictionary<string, object>)>();
            public List<(int lcdIndex, int brightness)> Screens = new List<(int, int)>();
        }

        public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
        {
            if (!await PingAsync(ct)) return ProbeResult.Of(DeviceState.NotRunning, "L-Connect servisi yanıt vermiyor");
            var plan = await BuildPlanAsync(ct);
            var lit = plan.Lighting.Any(l => Brightness(l.body) > 0);
            return ProbeResult.Ready($"{plan.Lighting.Count} ışık grubu, {plan.Screens.Count} LCD", lit);
        }

        public async Task<Dictionary<string, object>> TurnOffAsync(Dictionary<string, object> previous, CancellationToken ct)
        {
            var plan = await BuildPlanAsync(ct);
            var litNow = plan.Lighting.Any(l => Brightness(l.body) > 0) || plan.Screens.Any(s => s.brightness > 0);

            await SendLightingAsync(plan.TxPath, plan.Lighting.Select(l => (l.type, WithBrightness(l.type, l.body, 0))).ToList(), ct);
            foreach (var (lcdIndex, _) in plan.Screens)
                await SendAsync(plan.LcdPath, "SetBrightness", LcdBody(lcdIndex, 0), ct);

            if (!litNow && previous != null) return previous;
            return new Dictionary<string, object>
            {
                ["lighting"] = plan.Lighting.Select(l => new Dictionary<string, object> { ["type"] = l.type, ["body"] = l.body }).ToList(),
                ["screens"] = plan.Screens.Select(s => new Dictionary<string, object> { ["lcdIndex"] = s.lcdIndex, ["brightness"] = s.brightness }).ToList(),
            };
        }

        public async Task TurnOnAsync(Dictionary<string, object> snapshot, CancellationToken ct)
        {
            // L-Connect's saved bodies are the user's current intent (they only change when the
            // user presses Apply in L-Connect), so prefer them; fall back to our snapshot for any
            // group whose saved brightness is 0.
            var plan = await BuildPlanAsync(ct);
            var saved = SnapshotLighting(snapshot);
            var restore = plan.Lighting.Select(l =>
                Brightness(l.body) == 0 && saved.TryGetValue(Key(l.type, l.body), out var fromSnapshot) ? (l.type, fromSnapshot) : l).ToList();
            await SendLightingAsync(plan.TxPath, restore, ct);

            var savedScreens = SnapshotScreens(snapshot);
            foreach (var (lcdIndex, brightness) in plan.Screens)
            {
                var value = brightness > 0 ? brightness : savedScreens.TryGetValue(lcdIndex, out var b) && b > 0 ? b : 100;
                await SendAsync(plan.LcdPath, "SetBrightness", LcdBody(lcdIndex, value), ct);
            }
        }

        async Task<Plan> BuildPlanAsync(CancellationToken ct)
        {
            var controllers = Json.Obj(await PostAsync("?action=SyncControllerList", null, ct))
                ?? throw new InvalidOperationException("L-Connect servisi henüz hazır değil");
            var plan = new Plan
            {
                TxPath = controllers.FirstOrDefault(kv => AsInt(kv.Value) == WirelessTxType).Key,
                LcdPath = controllers.FirstOrDefault(kv => AsInt(kv.Value) == WirelessLcdType).Key,
            };
            if (plan.TxPath == null) throw new InvalidOperationException("kablosuz alıcı bulunamadı");

            var profile = LoadProfile(plan.TxPath);
            if (profile.Bool("IsAllMerged") == true || profile.Bool("IsTLV2Merged") == true)
                throw new NotSupportedException("birleşik (merge) ışık modu henüz desteklenmiyor");

            foreach (var group in ReadUnbindSettings("Fan"))
            {
                var settings = Json.Obj(group.Get("LightingSetting"));
                if (settings == null) continue;
                var scopes = group.Int("Scope") == ScopeAll ? new[] { "TLAll" } : new[] { "UP", "DOWM" };
                foreach (var scope in scopes)
                    if (Json.Obj(settings.Get(scope)) is Dictionary<string, object> body)
                        plan.Lighting.Add(("FanLightingSetting", body));
            }

            foreach (var strimer in ReadUnbindSettings("Strimer"))
            {
                if (strimer.Bool("IsSeparate") == true)
                    plan.Lighting.Add(("StrSingleLightSetting", new Dictionary<string, object>
                    {
                        ["MacStr"] = Json.Obj(strimer.Get("AllLightingConfig")).Str("MacStr"),
                        ["LightEffectSetArray"] = strimer.Get("LightEffectSetArray"),
                    }));
                else if (Json.Obj(strimer.Get("AllLightingConfig")) is Dictionary<string, object> all)
                    plan.Lighting.Add(("StrLightingSetting", all));
            }

            if (plan.LcdPath != null && Json.List(profile.Get("SubProfiles")) is IList subProfiles)
                foreach (var sub in subProfiles.OfType<Dictionary<string, object>>())
                    if (Json.List(sub.Get("BindLcd"))?.OfType<bool>().Any(b => b) == true && sub.Int("LcdGroup") is int lcd)
                        plan.Screens.Add((lcd, sub.Int("Brightness") ?? 100));

            return plan;
        }

        static IEnumerable<Dictionary<string, object>> ReadUnbindSettings(string type)
        {
            var newest = Directory.EnumerateFiles(Path.Combine(DataDir, "device"), "*.0", SearchOption.AllDirectories)
                .Select(path => (path, doc: TryReadGzipJson(path)))
                .Where(f => f.doc?.Str("DeviceID") == "LWireless-UnBindDevice-Setting" && f.doc.Str("Type") == type)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f.path))
                .Select(f => f.doc)
                .FirstOrDefault();
            return Json.Obj(newest?.Get("Data"))?.Values.OfType<Dictionary<string, object>>()
                ?? Enumerable.Empty<Dictionary<string, object>>();
        }

        static Dictionary<string, object> LoadProfile(string txPath)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var name = string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(txPath.ToLowerInvariant())).Select(b => b.ToString("x2")));
                return TryReadGzipJson(Path.Combine(DataDir, "profile", name)) ?? new Dictionary<string, object>();
            }
        }

        static Dictionary<string, object> TryReadGzipJson(string path)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzip, Encoding.UTF8))
                        return Json.ReadObject(reader.ReadToEnd());
                }
                catch (FileNotFoundException) { return null; }
                catch (Exception) { Thread.Sleep(150); }
            }
            return null;
        }

        static Dictionary<string, object> WithBrightness(string type, Dictionary<string, object> body, int value)
        {
            var copy = Json.ReadObject(Json.Write(body));
            if (type == "StrSingleLightSetting")
            {
                foreach (var effect in Json.List(copy.Get("LightEffectSetArray")).OfType<Dictionary<string, object>>())
                    effect["BrightnessType"] = value;
            }
            else copy["Brightness"] = value;
            return copy;
        }

        static int Brightness(Dictionary<string, object> body) =>
            body.Int("Brightness")
            ?? Json.List(body.Get("LightEffectSetArray"))?.OfType<Dictionary<string, object>>().Max(e => e.Int("BrightnessType") ?? 0)
            ?? 0;

        static string Key(string type, Dictionary<string, object> body) =>
            $"{type}|{body.Str("MacStr")}|{body.Int("Scope")}";

        static Dictionary<string, Dictionary<string, object>> SnapshotLighting(Dictionary<string, object> snapshot)
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            foreach (var item in Json.List(snapshot.Get("lighting"))?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                if (Json.Obj(item.Get("body")) is Dictionary<string, object> body && Brightness(body) > 0)
                    map[Key(item.Str("type"), body)] = body;
            return map;
        }

        static Dictionary<int, int> SnapshotScreens(Dictionary<string, object> snapshot)
        {
            var map = new Dictionary<int, int>();
            foreach (var item in Json.List(snapshot.Get("screens"))?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                if (item.Int("lcdIndex") is int index) map[index] = item.Int("brightness") ?? 100;
            return map;
        }

        static Dictionary<string, object> LcdBody(int lcdIndex, int value) => new Dictionary<string, object>
        {
            ["LcdIndex"] = lcdIndex,
            ["Position"] = new object[0],
            ["Data"] = value,
        };

        // The service answers `true` even when slv3 throws internally (seen once on the LCD fan
        // group), so check its log and resend the whole pass once if that happened.
        static async Task SendLightingAsync(string txPath, List<(string type, Dictionary<string, object> body)> requests, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var since = DateTime.UtcNow;
                foreach (var (type, body) in requests)
                    await SendAsync(txPath, type, body, ct);
                await Task.Delay(800, ct);
                if (!ServiceLoggedErrorSince(since)) return;
            }
            throw new InvalidOperationException("L-Connect servisi bir fan grubunda hata verdi");
        }

        static bool ServiceLoggedErrorSince(DateTime sinceUtc)
        {
            var log = Path.Combine(DataDir, "logs", $"L-Connect-Service-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(log)) return false;
            string tail;
            using (var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                file.Seek(Math.Max(0, file.Length - 65536), SeekOrigin.Begin);
                using (var reader = new StreamReader(file, Encoding.UTF8)) tail = reader.ReadToEnd();
            }
            foreach (var line in tail.Split('\n'))
            {
                if (!line.Contains("\"@l\":\"Error\"") || !line.Contains("[LWireless]")) continue;
                var start = line.IndexOf("\"@t\":\"", StringComparison.Ordinal);
                if (start < 0) continue;
                var stamp = line.Substring(start + 6, 28).TrimEnd('"', 'Z');
                if (DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
                    && at >= sinceUtc)
                    return true;
            }
            return false;
        }

        static async Task SendAsync(string devicePath, string type, Dictionary<string, object> body, CancellationToken ct)
        {
            if (devicePath == null) throw new InvalidOperationException("LCD cihazı bulunamadı");
            var encodedPath = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(devicePath)));
            var result = await PostAsync($"?action=Device&devicePath={encodedPath}&type={type}", body, ct);
            if (result is bool ok && !ok) throw new InvalidOperationException($"L-Connect '{type}' isteğini reddetti");
        }

        static async Task<bool> PingAsync(CancellationToken ct)
        {
            try { return await PostAsync("?action=Ping", null, ct) as string == "OK"; }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { return false; }
        }

        static async Task<object> PostAsync(string query, object body, CancellationToken ct)
        {
            var content = new StringContent(body == null ? "" : Json.Write(body), new UTF8Encoding(false), "application/json");
            using (var response = await Http.PostAsync(ServiceUrl + query, content, ct).ConfigureAwait(false))
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : Json.Read(text);
            }
        }

        static int? AsInt(object value) => value is int i ? i : value is long l ? (int?)l : value is decimal d ? (int?)d : null;
    }
}
