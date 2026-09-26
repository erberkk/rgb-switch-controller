using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PcControl.Core;

namespace PcControl.Controllers
{
    // TRYX KANALI (Panorama SE cooler screen). Its Chromium UI exposes nothing to UI Automation,
    // so the "Screen" switch in the Panorama SE page header is clicked by position and the result
    // is confirmed from KANALI's own store.json (panoramaSESettingConfig.screenEnable).
    public sealed class KanaliController : IDeviceController
    {
        // Device-independent offsets measured on KANALI 2.3.1.
        const double SidebarDeviceX = 140, SidebarDeviceY = 197;
        const double ScreenSwitchFromRight = 347, ScreenSwitchY = 85;

        static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kanali", "store.json");

        public string Id => "kanali";
        public string Title => "Soğutucu ekranı";
        public string Source => "KANALI · Panorama SE";
        public bool IsInstalled => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KANALI", "KANALI.exe"));
        public string Glyph => "";

        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (Process.GetProcessesByName("KANALI").Length == 0) return ProbeResult.Of(DeviceState.NotRunning, "KANALI çalışmıyor");
            if (!Installer.IsElevated) return ProbeResult.Of(DeviceState.NeedsSetup, "yönetici olarak çalıştırılmalı");
            var enabled = ScreenEnabled();
            return enabled == null
                ? ProbeResult.Of(DeviceState.Error, "ekran ayarı okunamadı")
                : ProbeResult.Ready(enabled.Value ? "ekran açık" : "ekran kapalı", enabled);
        }, ct);

        public Task<Dictionary<string, object>> TurnOffAsync(Dictionary<string, object> previous, CancellationToken ct) => Task.Run(() =>
        {
            if (ScreenEnabled() == false) return previous;
            SetScreen(false, ct);
            return new Dictionary<string, object> { ["screenEnable"] = true };
        }, ct);

        public Task TurnOnAsync(Dictionary<string, object> snapshot, CancellationToken ct) => Task.Run(() =>
        {
            if (snapshot?.Bool("screenEnable") == false || ScreenEnabled() == true) return;
            SetScreen(true, ct);
        }, ct);

        static void SetScreen(bool enable, CancellationToken ct)
        {
            if (!Installer.IsElevated) throw new InvalidOperationException("KANALI için yönetici yetkisi gerekiyor");
            var hwnd = Desktop.FindWindow("KANALI", "KANALI");
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("KANALI penceresi bulunamadı");

            var wasMinimized = Desktop.IsMinimized(hwnd) || !Desktop.IsVisible(hwnd);
            try
            {
                Desktop.BringToFront(hwnd);
                Thread.Sleep(wasMinimized ? 900 : 300);
                var scale = Desktop.Scale(hwnd);
                var (left, top, right, _) = Desktop.Bounds(hwnd);

                Desktop.ClickAt(left + (int)(SidebarDeviceX * scale), top + (int)(SidebarDeviceY * scale));
                Thread.Sleep(700);
                Desktop.BringToFront(hwnd);
                Desktop.ClickAt(right - (int)(ScreenSwitchFromRight * scale), top + (int)(ScreenSwitchY * scale));

                var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (ScreenEnabled() != enable)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > until) throw new InvalidOperationException("KANALI ekran anahtarı değişmedi");
                    Thread.Sleep(200);
                }
            }
            finally
            {
                if (wasMinimized) Desktop.Minimize(hwnd);
            }
        }

        static bool? ScreenEnabled()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string text;
                    using (var file = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(file)) text = reader.ReadToEnd();
                    return Json.Obj(Json.ReadObject(text).Get("panoramaSESettingConfig")).Bool("screenEnable");
                }
                catch (FileNotFoundException) { return null; }
                catch (Exception) { Thread.Sleep(120); }
            }
            return null;
        }
    }
}
