using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using PcControl.Core;

namespace PcControl.Controllers
{
    // Sapphire TRIXX exposes its tabs to UI Automation but not the Glow page content, so the
    // "RGB EFFECT STYLE" rows are clicked by position (relative to the fixed-size window) and
    // the selected row is read back from its highlight colour before and after every click.
    public sealed class TrixxController : IDeviceController
    {
        const string ExePath = @"C:\Program Files (x86)\Sapphire TRIXX\TRIXX.exe";
        const string WindowTitle = "Sapphire TriXX";

        // Measured on the 1256x1200 TRIXX 11.0 window.
        const double RefWidth = 1256, RefHeight = 1200;
        const double RowClickX = 200, RowSampleX = 65;
        static readonly string[] Styles =
            { "Runway", "Single Color", "Rainbow", "Serial", "Audio Visualization", "Custom Color", "External Source", "Turn off" };
        static readonly double[] RowY = { 377, 439, 500, 562, 623, 685, 746, 808 };
        static readonly int TurnOff = Array.IndexOf(Styles, "Turn off");
        static readonly int DefaultStyle = Array.IndexOf(Styles, "Custom Color");

        public string Id => "trixx";
        public string Title => "Ekran kartı";
        public string Source => "Sapphire TRIXX";
        public bool IsInstalled => System.IO.File.Exists(ExePath);
        public string Glyph => "\uE7F8";

        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (!System.IO.File.Exists(ExePath)) return ProbeResult.Of(DeviceState.Error, "TRIXX kurulu değil");
            if (!Installer.IsElevated) return ProbeResult.Of(DeviceState.NeedsSetup, "yönetici olarak çalıştırılmalı");
            var running = Process.GetProcessesByName("TRIXX").Length > 0;
            return ProbeResult.Ready(running ? "Glow" : "gerektiğinde açılır");
        }, ct);

        public Task<Dictionary<string, object>> TurnOffAsync(Dictionary<string, object> previous, CancellationToken ct) => Task.Run(() =>
            WithGlowPage(ct, window =>
            {
                var current = ReadSelected(window);
                if (current == TurnOff) return previous;
                Select(window, TurnOff, ct);
                return new Dictionary<string, object> { ["style"] = Styles[current] };
            }), ct);

        public Task TurnOnAsync(Dictionary<string, object> snapshot, CancellationToken ct) => Task.Run(() =>
            WithGlowPage<object>(ct, window =>
            {
                if (ReadSelected(window) != TurnOff) return null;
                var target = Array.IndexOf(Styles, snapshot?.Str("style"));
                Select(window, target < 0 || target == TurnOff ? DefaultStyle : target, ct);
                return null;
            }), ct);

        static T WithGlowPage<T>(CancellationToken ct, Func<AutomationElement, T> action)
        {
            if (!Installer.IsElevated) throw new InvalidOperationException("TRIXX için yönetici yetkisi gerekiyor");
            var startedHere = Process.GetProcessesByName("TRIXX").Length == 0;
            if (startedHere) Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(ExePath) });

            var window = Desktop.WaitFor(FindWindow, TimeSpan.FromSeconds(60), ct)
                ?? throw new InvalidOperationException("TRIXX penceresi açılmadı");
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            try
            {
                Desktop.BringToFront(hwnd);
                var glow = Desktop.WaitFor(() => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "HeaderGlow")), TimeSpan.FromSeconds(20), ct)
                    ?? throw new InvalidOperationException("TRIXX'te GLOW sekmesi bulunamadı");
                var item = (SelectionItemPattern)glow.GetCurrentPattern(SelectionItemPattern.Pattern);
                if (!item.Current.IsSelected) item.Select();

                // Wait until the Glow page is drawn: exactly one effect row is highlighted.
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (TryReadSelected(window) < 0)
                {
                    if (DateTime.UtcNow > until) throw new InvalidOperationException("TRIXX Glow sayfası beklenen düzende değil");
                    Thread.Sleep(200);
                }
                return action(window);
            }
            finally
            {
                if (startedHere) CloseWindow(window);
            }
        }

        static AutomationElement FindWindow()
        {
            foreach (var p in Process.GetProcessesByName("TRIXX"))
            {
                var match = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id),
                    new PropertyCondition(AutomationElement.NameProperty, WindowTitle)));
                if (match != null) return match;
            }
            return null;
        }

        static void Select(AutomationElement window, int style, CancellationToken ct)
        {
            Desktop.BringToFront(new IntPtr(window.Current.NativeWindowHandle));
            Thread.Sleep(150);
            var (x, y) = ToScreen(window, RowClickX, RowY[style]);
            Desktop.ClickAt(x, y);

            var until = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (TryReadSelected(window) != style)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > until) throw new InvalidOperationException($"TRIXX '{Styles[style]}' seçimini göstermedi");
                Thread.Sleep(150);
            }
        }

        static int ReadSelected(AutomationElement window)
        {
            var index = TryReadSelected(window);
            if (index < 0) throw new InvalidOperationException("TRIXX'te seçili efekt okunamadı");
            return index;
        }

        // Returns the highlighted row, or -1 when none or several rows look highlighted.
        static int TryReadSelected(AutomationElement window)
        {
            var r = window.Current.BoundingRectangle;
            if (r.IsEmpty || Math.Abs(r.Width / r.Height - RefWidth / RefHeight) > 0.03)
                throw new InvalidOperationException("TRIXX penceresi beklenmeyen boyutta");

            var hits = new List<int>();
            for (var i = 0; i < RowY.Length; i++)
            {
                var (x, y) = ToScreen(window, RowSampleX, RowY[i]);
                if (IsHighlight(PixelAt(x, y))) hits.Add(i);
            }
            return hits.Count == 1 ? hits[0] : -1;
        }

        static bool IsHighlight(Color c) => c.B > 0xDC && c.G > 0x9C && c.G < 0xE0 && c.R > 0x40 && c.R < 0x98;

        static (int x, int y) ToScreen(AutomationElement window, double refX, double refY)
        {
            var r = window.Current.BoundingRectangle;
            return ((int)(r.Left + refX / RefWidth * r.Width), (int)(r.Top + refY / RefHeight * r.Height));
        }

        static Color PixelAt(int x, int y)
        {
            using (var bmp = new Bitmap(1, 1))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
                return bmp.GetPixel(0, 0);
            }
        }

        static void CloseWindow(AutomationElement window)
        {
            try
            {
                var close = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ButtonClose"));
                var button = close?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                ((InvokePattern)button?.GetCurrentPattern(InvokePattern.Pattern))?.Invoke();
            }
            catch (Exception e) when (e is ElementNotAvailableException || e is InvalidOperationException) { }
        }
    }
}
