using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Xml.Linq;
using Microsoft.Win32;
using RgbSwitch.Core;

namespace RgbSwitch.Controllers
{
    // Drives GIGABYTE Control Center's own "RGB Fusion" page (motherboard + RAM in sync mode)
    // by clicking its pattern buttons, then checks the result in GCC's usdata2.xml.
    public sealed class GccController : IDeviceController
    {
        const string WindowTitle = "GIGABYTE CONTROL CENTER";
        const uint ShowMessage = 0x9990;
        const uint HideMessage = 0x9991;
        const int ModeOff = 8;

        static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GIGABYTE", "Control Center");

        static readonly Dictionary<int, string> ModeButtons = new Dictionary<int, string>
        {
            [0] = "STATIC", [1] = "PULSE", [4] = "FLASH", [11] = "DFLASH", [9] = "CYCLE", [69] = "RAINBOW WAVE", [ModeOff] = "OFF",
        };

        public string Id => "gcc";
        public string Title => "Anakart ve RAM";
        public string Source => "GIGABYTE Control Center";
        public bool IsInstalled => File.Exists(Path.Combine(InstallDir, "GCC.exe"));
        public string Glyph => "";

        sealed class SyncSetting
        {
            public int Mode;
            public string Color, Speed, Brightness;
        }

        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (Process.GetProcessesByName("GCC").Length == 0) return ProbeResult.Of(DeviceState.NotRunning, "GCC çalışmıyor");
            if (!Installer.IsElevated) return ProbeResult.Of(DeviceState.NeedsSetup, "yönetici olarak çalıştırılmalı");
            if (!BothDevicesSynced()) return ProbeResult.Of(DeviceState.Error, "anakart ve RAM senkron değil");
            var setting = ReadSetting();
            var name = ModeButtons.TryGetValue(setting.Mode, out var n) ? n : $"mod {setting.Mode}";
            return ProbeResult.Ready($"RGB Fusion · {name}", setting.Mode != ModeOff);
        }, ct);

        public Task<Dictionary<string, object>> TurnOffAsync(Dictionary<string, object> previous, CancellationToken ct) => Task.Run(() =>
        {
            Precheck();
            var before = ReadSetting();
            if (before.Mode == ModeOff) return previous;
            Apply(ModeOff, ct);
            return new Dictionary<string, object>
            {
                ["mode"] = before.Mode, ["color"] = before.Color, ["sp"] = before.Speed, ["bri"] = before.Brightness,
            };
        }, ct);

        public Task TurnOnAsync(Dictionary<string, object> snapshot, CancellationToken ct) => Task.Run(() =>
        {
            Precheck();
            var mode = snapshot?.Int("mode") ?? 0;
            if (!ModeButtons.ContainsKey(mode) || mode == ModeOff) mode = 0;
            if (ReadSetting().Mode != ModeOff) return;
            Apply(mode, ct);
            var color = snapshot?.Str("color");
            if (color != null && !string.Equals(ReadSetting().Color, color, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"GCC rengi {color} yerine {ReadSetting().Color} oldu");
        }, ct);

        static void Precheck()
        {
            if (!Installer.IsElevated) throw new InvalidOperationException("GCC için yönetici yetkisi gerekiyor");
            if (Process.GetProcessesByName("GCC").Length == 0) throw new InvalidOperationException("GCC çalışmıyor");
            if (!BothDevicesSynced()) throw new NotSupportedException("anakart ve RAM RGB Fusion'da senkron değil");
        }

        static void Apply(int mode, CancellationToken ct)
        {
            var hwnd = Desktop.FindWindow("GCC", WindowTitle);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("GCC penceresi bulunamadı");
            var wasVisible = Desktop.IsVisible(hwnd);
            try
            {
                if (!wasVisible) Desktop.Post(hwnd, ShowMessage);
                var window = Desktop.WaitFor(() => Desktop.IsVisible(hwnd) ? AutomationElement.FromHandle(hwnd) : null, TimeSpan.FromSeconds(5), ct)
                    ?? throw new InvalidOperationException("GCC penceresi açılmadı");
                Desktop.BringToFront(hwnd);

                var buttonText = ModeButtons[mode];
                var pattern = Desktop.FindVisibleByName(window, buttonText, ControlType.Text);
                if (pattern == null)
                {
                    var tab = Desktop.WaitFor(() => Desktop.FindVisibleByName(window, "RGB Fusion", ControlType.Button), TimeSpan.FromSeconds(3), ct)
                        ?? throw new InvalidOperationException("GCC'de 'RGB Fusion' sekmesi bulunamadı");
                    Desktop.Click(tab);
                    pattern = Desktop.WaitFor(() => Desktop.FindVisibleByName(window, buttonText, ControlType.Text), TimeSpan.FromSeconds(5), ct)
                        ?? throw new InvalidOperationException($"GCC'de '{buttonText}' düğmesi bulunamadı");
                }

                Desktop.BringToFront(hwnd);
                Thread.Sleep(150);
                Desktop.Click(pattern);

                var until = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                while (ReadSetting().Mode != mode)
                {
                    if (DateTime.UtcNow > until) throw new InvalidOperationException($"GCC '{buttonText}' tıklamasını kaydetmedi");
                    Thread.Sleep(150);
                }
            }
            finally
            {
                if (!wasVisible) Desktop.Post(hwnd, HideMessage);
            }
        }

        static SyncSetting ReadSetting()
        {
            var path = Path.Combine(InstallDir, "usdata2.xml");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    XDocument doc;
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        doc = XDocument.Load(file);
                    var param = doc.Root?.Element("sync_setting")?.Element("param")
                        ?? throw new InvalidDataException("usdata2.xml içinde sync_setting yok");
                    return new SyncSetting
                    {
                        Mode = int.Parse((string)param.Attribute("mode") ?? "0"),
                        Color = (string)param.Attribute("color"),
                        Speed = (string)param.Attribute("sp"),
                        Brightness = (string)param.Attribute("bri"),
                    };
                }
                catch (Exception e) when (attempt < 5 && (e is IOException || e is System.Xml.XmlException))
                {
                    Thread.Sleep(120);
                }
            }
        }

        static bool BothDevicesSynced()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\GCC"))
            {
                var devices = Json.List(Json.Read(key?.GetValue("Sync_info") as string ?? "[]"));
                var categories = devices?.OfType<Dictionary<string, object>>()
                    .Where(d => d.Bool("isSync") == true)
                    .Select(d => Json.Obj(d.Get("Device_Cap")).Int("Device_Category_ID"))
                    .ToList();
                return categories != null && categories.Contains(0) && categories.Contains(2);
            }
        }
    }
}
