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
using PcControl.Core;

namespace PcControl
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

    public sealed class InputChip : Observable
    {
        public MonitorCard Card;
        public int Code;
        public string Name { get; set; }

        bool isCurrent;
        public bool IsCurrent { get => isCurrent; set { isCurrent = value; Raise(nameof(IsCurrent)); } }
    }

    public sealed class MonitorCard : Observable
    {
        public MonitorCard(MonitorInfo info)
        {
            Index = info.Index;
            Model = info.Model;
            CanPower = info.CanPowerOff;
            Inputs = info.Inputs.Select(i => new InputChip { Card = this, Code = i.Code, Name = i.Name, IsCurrent = i.Code == info.CurrentInput }).ToList();
            UpdateStatus();
        }

        public int Index { get; }
        public string Model { get; }
        public bool CanPower { get; }
        public string PowerTip => "Monitörü kapat (açmak için monitörün düğmesi gerekebilir)";
        public List<InputChip> Inputs { get; }

        string status;
        public string Status { get => status; set { status = value; Raise(nameof(Status)); } }

        public void Select(int code)
        {
            foreach (var chip in Inputs) chip.IsCurrent = chip.Code == code;
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var current = Inputs.FirstOrDefault(i => i.IsCurrent);
            Status = current == null ? "giriş okunamadı" : $"Şu an {current.Name}";
        }
    }

    static class Palette
    {
        public static readonly Brush Ok = Freeze(Colors.White);
        public static readonly Brush Warn = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));
        public static readonly Brush Idle = Freeze(Color.FromRgb(0x3A, 0x3A, 0x3A));

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
        readonly ObservableCollection<MonitorCard> monitors = new ObservableCollection<MonitorCard>();
        Storyboard spin;
        bool busy;
        DateTime lastProbe = DateTime.MinValue;

        public MainWindow(LightingService service)
        {
            InitializeComponent();
            this.service = service;
            rows = new ObservableCollection<DeviceRow>(service.Controllers.Select(c => new DeviceRow(c)));
            DeviceList.ItemsSource = rows;
            MonitorList.ItemsSource = monitors;
            foreach (var row in rows) row.Show(ProbeResult.Of(DeviceState.Unknown, "kontrol ediliyor…"));

            Loaded += async (_, __) => { ApplyVisual(service.LightsOff, animate: false); await RefreshAllAsync(); };
            Activated += async (_, __) =>
            {
                if (busy || DateTime.Now - lastProbe < TimeSpan.FromSeconds(5)) return;
                service.Reload();
                ApplyVisual(service.LightsOff, animate: true);
                await RefreshAllAsync();
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
                foreach (var outcome in outcomes.Where(o => !o.Ok))
                    rows.First(r => r.Controller == outcome.Controller).ShowError(outcome.Error);
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
            await ProbeDevicesAsync(keepErrors: true);
        }

        async Task RefreshAllAsync()
        {
            await Task.WhenAll(ProbeDevicesAsync(), RefreshMonitorsAsync());
        }

        async Task ProbeDevicesAsync(bool keepErrors = false)
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

        async Task RefreshMonitorsAsync()
        {
            List<MonitorInfo> found;
            try { found = await Task.Run(() => Monitors.List()); }
            catch (Exception e) { Footer.Text = "Monitörler okunamadı: " + e.Message; return; }
            monitors.Clear();
            foreach (var info in found.Where(m => m.Inputs.Count > 0 || m.CanPowerOff)) monitors.Add(new MonitorCard(info));
            NoMonitors.Visibility = monitors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async void OnMonitorInput(object sender, RoutedEventArgs e)
        {
            var chip = (InputChip)((FrameworkElement)sender).Tag;
            if (chip.IsCurrent) return;
            try
            {
                await Task.Run(() => Monitors.SetInput(chip.Card.Index, chip.Code));
                chip.Card.Select(chip.Code);
                Footer.Text = $"{chip.Card.Model} → {chip.Name}";
            }
            catch (Exception ex) { Footer.Text = $"{chip.Card.Model}: {ex.Message}"; }
        }

        async void OnMonitorPower(object sender, RoutedEventArgs e)
        {
            var card = (MonitorCard)((FrameworkElement)sender).Tag;
            try
            {
                await Task.Run(() => Monitors.SetPower(card.Index, on: false));
                Footer.Text = $"{card.Model} kapatıldı";
                await Task.Delay(2500);
                await RefreshMonitorsAsync();
            }
            catch (Exception ex) { Footer.Text = $"{card.Model}: {ex.Message}"; }
        }

        void OnSleepDisplays(object sender, RoutedEventArgs e) => Monitors.SleepAll();

        async void OnRefresh(object sender, RoutedEventArgs e)
        {
            service.Reload();
            ApplyVisual(service.LightsOff, animate: true);
            await RefreshAllAsync();
        }

        void ApplyVisual(bool off, bool animate)
        {
            StateTitle.Text = off ? "Işıklar kapalı" : "Işıklar açık";
            StateHint.Text = off ? "Açmak için dokun" : "Kapatmak için dokun";

            var d = animate ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
            DiscFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(off ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White, d));
            DiscStroke.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(off ? Color.FromRgb(0x33, 0x33, 0x33) : Colors.White, d));
            GlyphFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(off ? Color.FromRgb(0x6A, 0x6A, 0x6A) : Colors.Black, d));
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(off ? 0 : 1, d));
        }

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
