using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RgbSwitch.Core;

namespace RgbSwitch
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class DeviceRow : Observable
    {
        public DeviceRow(IDeviceController controller) { Controller = controller; }

        public IDeviceController Controller { get; }
        public string Glyph => Controller.Glyph;
        public string Title => Controller.Title;

        string subtitle;
        public string Subtitle { get => subtitle; set { subtitle = value; Raise(nameof(Subtitle)); } }

        string statusText;
        public string StatusText { get => statusText; set { statusText = value; Raise(nameof(StatusText)); } }

        Brush statusBrush = Palette.Idle;
        public Brush StatusBrush { get => statusBrush; set { statusBrush = value; Raise(nameof(StatusBrush)); } }

        public void Show(ProbeResult probe)
        {
            Subtitle = string.IsNullOrEmpty(probe.Detail) ? Controller.Source : $"{Controller.Source} · {probe.Detail}";
            StatusBrush = Palette.For(probe.State);
            StatusText = Palette.Label(probe.State);
        }

        public void ShowError(Exception e)
        {
            Subtitle = $"{Controller.Source} · {e.Message}";
            StatusBrush = Palette.Error;
            StatusText = "Hata";
        }
    }

    public sealed class TargetChip : Observable
    {
        public IDeviceController Controller;
        public string Title { get; set; }

        bool selected = true;
        public bool Selected { get => selected; set { selected = value; Raise(nameof(Selected)); } }
    }

    static class Palette
    {
        public static readonly Brush Ok = Freeze(Color.FromRgb(0x30, 0xD1, 0x58));
        public static readonly Brush Warn = Freeze(Color.FromRgb(0xFF, 0xD6, 0x0A));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xFF, 0x45, 0x3A));
        public static readonly Brush Idle = Freeze(Color.FromRgb(0x3A, 0x3A, 0x3F));

        static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Brush For(DeviceState s) =>
            s == DeviceState.Ready ? Ok : s == DeviceState.NeedsSetup ? Warn : s == DeviceState.Error ? Error : Idle;

        public static string Label(DeviceState s) =>
            s == DeviceState.Ready ? "Hazır" : s == DeviceState.NeedsSetup ? "Kurulum gerekli"
            : s == DeviceState.NotRunning ? "Uygulama kapalı" : s == DeviceState.Error ? "Hata" : "Bilinmiyor";
    }

    public partial class MainWindow : Window
    {
        static readonly string[] PresetColors =
            { "FFFFFF", "FFB46B", "FF3B30", "FF9500", "FFD60A", "30D158", "64D2FF", "0A84FF", "BF5AF2", "FF375F" };

        readonly LightingService service;
        readonly ObservableCollection<DeviceRow> rows;
        readonly List<TargetChip> targets;
        Storyboard spin;
        bool busy;
        DateTime lastProbe = DateTime.MinValue;

        // Picker state in HSV, the colour currently shown in the picker.
        double hue, saturation, value;
        bool dragging;

        public MainWindow(LightingService service)
        {
            InitializeComponent();
            this.service = service;
            rows = new ObservableCollection<DeviceRow>(service.Controllers.Select(c => new DeviceRow(c)));
            DeviceList.ItemsSource = rows;
            targets = service.ColorTargets.Select(c => new TargetChip { Controller = c, Title = c.Title }).ToList();
            TargetList.ItemsSource = targets;
            foreach (var row in rows) row.Show(ProbeResult.Of(DeviceState.Unknown, "kontrol ediliyor…"));

            foreach (var hex in PresetColors)
            {
                Rgb.TryParse(hex, out var rgb);
                var swatch = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(ToColor(rgb)), Tag = rgb, ToolTip = "#" + hex };
                swatch.Click += (s, e) => SetPicker((Rgb)((Button)s).Tag);
                Presets.Children.Add(swatch);
            }

            SvArea.SizeChanged += (_, __) => UpdatePicker();
            Rgb.TryParse(service.Color ?? "FFB46B", out var initial);
            Loaded += async (_, __) =>
            {
                SetPicker(initial);
                ApplyAccent(initial);
                ApplyVisual(service.LightsOff, animate: false);
                await ProbeAllAsync();
            };
            Activated += async (_, __) =>
            {
                if (busy || DateTime.Now - lastProbe < TimeSpan.FromSeconds(5)) return;
                service.Reload();
                ApplyVisual(service.LightsOff, animate: true);
                await ProbeAllAsync();
            };
        }

        public event EventHandler LightsChanged;

        Rgb PickedColor => HsvToRgb(hue, saturation, value);

        public async Task ToggleAsync()
        {
            if (busy) return;
            busy = true;
            SetSpinner(true);
            var targetOff = !service.LightsOff;
            StateHint.Text = targetOff ? "Kapatılıyor…" : "Açılıyor…";
            try
            {
                var outcomes = await Task.Run(() => service.ToggleAsync(CancellationToken.None));
                ShowOutcomes(outcomes, targetOff ? "kapatıldı" : "açıldı");
            }
            catch (Exception e)
            {
                Footer.Text = "İşlem başarısız: " + e.Message;
            }
            finally
            {
                SetSpinner(false);
                ApplyVisual(service.LightsOff, animate: true);
                busy = false;
                LightsChanged?.Invoke(this, EventArgs.Empty);
            }
            await ProbeAllAsync(keepErrors: true);
        }

        async void OnApplyColor(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var chosen = targets.Where(t => t.Selected).Select(t => t.Controller).ToList();
            if (chosen.Count == 0) { Footer.Text = "Renk için en az bir cihaz seç"; return; }
            busy = true;
            ApplyButton.IsEnabled = false;
            PowerButton.IsEnabled = false;
            ApplyText.Text = "Uygulanıyor…";
            var color = PickedColor;
            try
            {
                var outcomes = await Task.Run(() => service.ApplyColorAsync(color, chosen, CancellationToken.None));
                ApplyAccent(color);
                ShowOutcomes(outcomes, $"{color} rengine geçti");
            }
            catch (Exception ex)
            {
                Footer.Text = "Renk uygulanamadı: " + ex.Message;
            }
            finally
            {
                ApplyText.Text = "Tümüne uygula";
                ApplyButton.IsEnabled = !service.LightsOff;
                PowerButton.IsEnabled = true;
                busy = false;
            }
            await ProbeAllAsync(keepErrors: true);
        }

        void ShowOutcomes(List<DeviceOutcome> outcomes, string verb)
        {
            foreach (var outcome in outcomes.Where(o => !o.Ok))
                rows.First(r => r.Controller == outcome.Controller).ShowError(outcome.Error);
            var failed = outcomes.Where(o => !o.Ok).ToList();
            Footer.Text = failed.Count == 0
                ? $"{outcomes.Count} cihaz {verb} · {DateTime.Now:HH:mm}"
                : $"{outcomes.Count - failed.Count}/{outcomes.Count} cihaz tamam · sorun: {string.Join(", ", failed.Select(f => f.Controller.Title))}";
        }

        async Task ProbeAllAsync(bool keepErrors = false)
        {
            lastProbe = DateTime.Now;
            await Task.WhenAll(rows.Select(async row =>
            {
                if (keepErrors && row.StatusBrush == Palette.Error) return;
                try { row.Show(await Task.Run(() => row.Controller.ProbeAsync(CancellationToken.None))); }
                catch (Exception e) { row.ShowError(e); }
            }));
            if (!busy && string.IsNullOrEmpty(Footer.Text) && service.ChangedAt is DateTime at)
                Footer.Text = $"Son değişiklik: {at:dd.MM HH:mm}";
        }

        // ---- colour picker ----

        void OnSvDown(object sender, MouseButtonEventArgs e) { dragging = true; SvArea.CaptureMouse(); PickSv(e.GetPosition(SvArea)); }

        void OnSvMove(object sender, MouseEventArgs e) { if (dragging && SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea)); }

        void OnHueDown(object sender, MouseButtonEventArgs e) { dragging = true; HueArea.CaptureMouse(); PickHue(e.GetPosition(HueArea)); }

        void OnHueMove(object sender, MouseEventArgs e) { if (dragging && HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea)); }

        void OnPickerUp(object sender, MouseButtonEventArgs e) { dragging = false; ((UIElement)sender).ReleaseMouseCapture(); }

        void PickSv(Point p)
        {
            saturation = Clamp(p.X / SvArea.ActualWidth);
            value = 1 - Clamp(p.Y / SvArea.ActualHeight);
            UpdatePicker();
        }

        void PickHue(Point p)
        {
            hue = Clamp(p.X / HueArea.ActualWidth) * 359.999;
            UpdatePicker();
        }

        void OnHexKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            OnHexCommit(sender, e);
            e.Handled = true;
        }

        void OnHexCommit(object sender, RoutedEventArgs e)
        {
            if (Rgb.TryParse(HexBox.Text, out var rgb)) SetPicker(rgb);
            else HexBox.Text = "#" + PickedColor.Hex;
        }

        void SetPicker(Rgb rgb)
        {
            (hue, saturation, value) = RgbToHsv(rgb);
            UpdatePicker();
        }

        void UpdatePicker()
        {
            var rgb = PickedColor;
            var color = ToColor(rgb);
            SvHue.Color = ToColor(HsvToRgb(hue, 1, 1));
            PreviewBrush.Color = color;
            ApplyBrush.Color = color;
            ApplyTextBrush.Color = Luminance(rgb) > 0.6 ? Colors.Black : Colors.White;
            if (!HexBox.IsKeyboardFocused) HexBox.Text = "#" + rgb.Hex;

            if (SvArea.ActualWidth > 0)
            {
                Canvas.SetLeft(SvThumb, saturation * SvArea.ActualWidth - SvThumb.Width / 2);
                Canvas.SetTop(SvThumb, (1 - value) * SvArea.ActualHeight - SvThumb.Height / 2);
                Canvas.SetLeft(HueThumb, hue / 360 * HueArea.ActualWidth - HueThumb.Width / 2);
            }
        }

        // The power button and title dot take the last applied colour.
        void ApplyAccent(Rgb rgb)
        {
            var c = ToColor(rgb);
            TitleDot.Color = c;
            accent = c;
            ApplyVisual(service.LightsOff, animate: true);
        }

        Color accent = Color.FromRgb(0xFF, 0xB4, 0x6B);

        void ApplyVisual(bool off, bool animate)
        {
            StateTitle.Text = off ? "Işıklar kapalı" : "Işıklar açık";
            StateHint.Text = off ? "Açmak için dokun" : "Kapatmak için dokun";
            ApplyButton.IsEnabled = !off && !busy;
            ApplyButton.ToolTip = off ? "Renk vermek için önce ışıkları aç" : null;

            var d = animate ? TimeSpan.FromMilliseconds(320) : TimeSpan.Zero;
            var dim = Color.FromRgb(0x3A, 0x3A, 0x3F);
            Animate(RingBrush, off ? dim : accent, d);
            Animate(GlyphBrush, off ? Color.FromRgb(0x5C, 0x5C, 0x63) : accent, d);
            HaloStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(off ? Colors.Transparent : Color.FromArgb(0x50, accent.R, accent.G, accent.B), d));
            HaloEdge.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0, accent.R, accent.G, accent.B), d));
        }

        static void Animate(SolidColorBrush brush, Color to, TimeSpan d) =>
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, d) { EasingFunction = new CubicEase() });

        static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

        static double Luminance(Rgb c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

        static Rgb HsvToRgb(double h, double s, double v)
        {
            var c = v * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = v - c;
            double r, g, b;
            if (h < 60) (r, g, b) = (c, x, 0);
            else if (h < 120) (r, g, b) = (x, c, 0);
            else if (h < 180) (r, g, b) = (0, c, x);
            else if (h < 240) (r, g, b) = (0, x, c);
            else if (h < 300) (r, g, b) = (x, 0, c);
            else (r, g, b) = (c, 0, x);
            byte B(double f) => (byte)Math.Round((f + m) * 255);
            return new Rgb(B(r), B(g), B(b));
        }

        static (double h, double s, double v) RgbToHsv(Rgb c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            return (h, max == 0 ? 0 : d / max, max);
        }

        // ---- window ----

        void SetSpinner(bool on)
        {
            PowerButton.IsEnabled = !on;
            Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (spin == null)
            {
                var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
                Storyboard.SetTarget(anim, Spinner);
                Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
                spin = new Storyboard();
                spin.Children.Add(anim);
            }
            if (on) spin.Begin(); else spin.Stop();
        }

        async void OnPowerClick(object sender, RoutedEventArgs e) => await ToggleAsync();

        async void OnRefresh(object sender, RoutedEventArgs e)
        {
            service.Reload();
            ApplyVisual(service.LightsOff, animate: true);
            await ProbeAllAsync();
        }

        void OnDragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void OnClose(object sender, RoutedEventArgs e) => Hide();
    }
}
