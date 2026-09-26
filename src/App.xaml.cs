using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RgbSwitch.Controllers;
using RgbSwitch.Core;
using Forms = System.Windows.Forms;

namespace RgbSwitch
{
    public partial class App : Application
    {
        const string InstanceName = @"Local\RgbSwitch.Instance";
        const string ShowEventName = @"Local\RgbSwitch.Show";

        Mutex instance;
        EventWaitHandle showSignal;
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem toggleItem;
        MainWindow window;
        LightingService service;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var command = e.Args.Select(a => a.TrimStart('-', '/').ToLowerInvariant()).FirstOrDefault();

            if (command == "install")
            {
                try { Installer.Install(); }
                catch (Exception ex) { MessageBox.Show("Kurulum başarısız: " + ex.Message, "RGB Switch"); }
                Shutdown();
                return;
            }

            if (!Installer.IsElevated && ForwardToElevatedTask(command))
            {
                Shutdown();
                return;
            }

            service = new LightingService(DeviceRegistry.All());
            if (command == "on" || command == "off" || command == "toggle")
            {
                RunHeadless(command);
                return;
            }

            instance = new Mutex(true, InstanceName, out var created);
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
                Shutdown();
                return;
            }

            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, __) => Dispatcher.BeginInvoke(new Action(ShowWindow)), null, -1, false);

            window = new MainWindow(service);
            window.LightsChanged += (_, __) => UpdateTray();
            CreateTray();
            if (command != "tray") ShowWindow();
        }

        // GCC's window only accepts input from an elevated process. The first launch registers the
        // elevated tasks (one UAC prompt); later launches just start them.
        static bool ForwardToElevatedTask(string command)
        {
            if (command == "on" || command == "off") return false;
            if (!Installer.TaskExists(Installer.GuiTask))
            {
                try { Installer.RequestInstall(); }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
            return Installer.RunTask(command == "toggle" ? Installer.ToggleTask : Installer.GuiTask);
        }

        void RunHeadless(string command)
        {
            var ct = CancellationToken.None;
            var run = command == "on" ? service.TurnOnAsync(ct) : command == "off" ? service.TurnOffAsync(ct) : service.ToggleAsync(ct);
            Task.Run(() => run).ContinueWith(t =>
            {
                var failed = t.IsFaulted || t.Result.Any(o => !o.Ok);
                Dispatcher.BeginInvoke(new Action(() => Shutdown(failed ? 1 : 0)));
            });
        }

        void CreateTray()
        {
            toggleItem = new Forms.ToolStripMenuItem("", null, async (_, __) => await window.ToggleAsync());
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(toggleItem);
            menu.Items.Add("Pencereyi göster", null, (_, __) => ShowWindow());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Çıkış", null, (_, __) => ExitApp());

            tray = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowWindow(); };
            UpdateTray();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            var off = service.LightsOff;
            toggleItem.Text = off ? "Işıkları aç" : "Işıkları kapat";
            tray.Text = off ? "RGB Switch · ışıklar kapalı" : "RGB Switch · ışıklar açık";
            var old = tray.Icon;
            tray.Icon = TrayIcon.Create(lit: !off);
            old?.Dispose();
        }

        void ShowWindow()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        void ExitApp()
        {
            tray.Visible = false;
            tray.Dispose();
            Shutdown();
        }
    }

    static class TrayIcon
    {
        public static Icon Create(bool lit)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(1, 1, 30, 30);
                using (var fill = lit
                    ? (Brush)new LinearGradientBrush(rect, Color.FromArgb(0x8B, 0x5C, 0xF6), Color.FromArgb(0x22, 0xD3, 0xEE), 45f)
                    : new SolidBrush(Color.FromArgb(0x2A, 0x2F, 0x3A)))
                    g.FillEllipse(fill, rect);
                using (var pen = new Pen(lit ? Color.White : Color.FromArgb(0x9C, 0xA3, 0xAF), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, 9, 9, 14, 14, -60, 300);
                    g.DrawLine(pen, 16, 7, 16, 15);
                }
                var handle = bmp.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }
}
