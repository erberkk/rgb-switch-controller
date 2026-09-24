using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RgbSwitch.Core;

namespace RgbSwitch
{
    public sealed class DeviceRow : INotifyPropertyChanged
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

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    static class Palette
    {
        public static readonly Brush Ok = Freeze(Color.FromRgb(0x34, 0xD3, 0x99));
        public static readonly Brush Warn = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));
        public static readonly Brush Idle = Freeze(Color.FromRgb(0x4B, 0x52, 0x60));

        static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Brush For(DeviceState s) =>
            s == DeviceState.Ready ? Ok : s == DeviceState.NeedsSetup ? Warn : s == DeviceState.Error ? Error : Idle;

        public static string Label(DeviceState s) =>
            s == DeviceState.Ready ? "Hazır" : s == DeviceState.NeedsSetup ? "Kurulum gerekli"
            : s == DeviceState.NotRunning ? "Uygulama kapalı" : s == DeviceState.Error ? "Hata" : "Bilinmiyor";
    }

    public partial class MainWindow : Window
    {
        readonly LightingService service;
        readonly ObservableCollection<DeviceRow> rows;
        Storyboard spin;
        bool busy;
        DateTime lastProbe = DateTime.MinValue;

        public MainWindow(LightingService service)
        {
            InitializeComponent();
            this.service = service;
            rows = new ObservableCollection<DeviceRow>(service.Controllers.Select(c => new DeviceRow(c)));
            DeviceList.ItemsSource = rows;
            foreach (var row in rows) row.Show(ProbeResult.Of(DeviceState.Unknown, "kontrol ediliyor…"));

            Loaded += async (_, __) => { ApplyVisual(service.LightsOff, animate: false); await ProbeAllAsync(); };
            Activated += async (_, __) =>
            {
                if (busy || DateTime.Now - lastProbe < TimeSpan.FromSeconds(5)) return;
                service.Reload();
                ApplyVisual(service.LightsOff, animate: true);
                await ProbeAllAsync();
            };
        }

        public event EventHandler LightsChanged;

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
                foreach (var outcome in outcomes)
                {
                    var row = rows.First(r => r.Controller == outcome.Controller);
                    if (!outcome.Ok) row.ShowError(outcome.Error);
                }
                var failed = outcomes.Where(o => !o.Ok).ToList();
                Footer.Text = failed.Count == 0
                    ? $"{outcomes.Count} cihazın tamamı {(targetOff ? "kapatıldı" : "açıldı")} · {DateTime.Now:HH:mm}"
                    : $"{outcomes.Count - failed.Count}/{outcomes.Count} cihaz tamam · sorun: {string.Join(", ", failed.Select(f => f.Controller.Title))}";
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

        void ApplyVisual(bool off, bool animate)
        {
            StateTitle.Text = off ? "Işıklar kapalı" : "Işıklar açık";
            StateHint.Text = off ? "Açmak için dokun" : "Kapatmak için dokun";

            var a = off ? Color.FromRgb(0x1B, 0x1F, 0x27) : Color.FromRgb(0x8B, 0x5C, 0xF6);
            var b = off ? Color.FromRgb(0x15, 0x18, 0x1E) : Color.FromRgb(0x22, 0xD3, 0xEE);
            var halo = off ? Color.FromArgb(0x00, 0x8B, 0x5C, 0xF6) : Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6);
            var stroke = off ? Color.FromRgb(0x2A, 0x2F, 0x3A) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            var glyph = off ? Color.FromRgb(0x6B, 0x72, 0x80) : Colors.White;

            var d = animate ? TimeSpan.FromMilliseconds(350) : TimeSpan.Zero;
            Animate(DiscA, a, d);
            Animate(DiscB, b, d);
            Animate(HaloInner, halo, d);
            DiscStroke.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(stroke, d));
            PowerGlyph.Foreground = new SolidColorBrush(glyph);
        }

        static void Animate(GradientStop stop, Color to, TimeSpan d) =>
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, d) { EasingFunction = new CubicEase() });

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

        void OnDragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void OnClose(object sender, RoutedEventArgs e) => Hide();
    }
}
