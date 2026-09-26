using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace RgbSwitch.Core
{
    // Window lookup, UI Automation search and real mouse clicks, for vendor apps whose
    // buttons only react to physical input.
    public static class Desktop
    {
        public static IntPtr FindWindow(string processName, string title)
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName(processName).Select(p => (uint)p.Id));
            var found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains(pid)) return true;
                var text = new StringBuilder(256);
                GetWindowText(h, text, text.Capacity);
                if (text.ToString() != title) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

        public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

        public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, 6);

        public static double Scale(IntPtr hwnd) => GetDpiForWindow(hwnd) / 96.0;

        public static (int left, int top, int right, int bottom) Bounds(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out var r);
            return (r.Left, r.Top, r.Right, r.Bottom);
        }

        public static void Post(IntPtr hwnd, uint message) => PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);

        public static void BringToFront(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            // Windows only lets the foreground process hand focus over; a synthetic Alt tap
            // satisfies that rule for our own (elevated) process.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        }

        public static AutomationElement WaitFor(Func<AutomationElement> find, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                AutomationElement element = null;
                try { element = find(); } catch (ElementNotAvailableException) { }
                if (element != null) return element;
                if (DateTime.UtcNow > until) return null;
                Thread.Sleep(150);
            }
        }

        public static AutomationElement FindVisibleByName(AutomationElement root, string name, ControlType type = null)
        {
            Condition condition = new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase);
            if (type != null) condition = new AndCondition(condition, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            return root.FindAll(TreeScope.Descendants, condition)
                .Cast<AutomationElement>()
                .FirstOrDefault(e => !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty);
        }

        public static void Click(AutomationElement element)
        {
            var r = element.Current.BoundingRectangle;
            if (r.IsEmpty) throw new InvalidOperationException("tıklanacak öğe görünmüyor");
            ClickAt((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        }

        public static void ClickAt(int x, int y)
        {
            GetCursorPos(out var original);
            try
            {
                SetCursorPos(x, y);
                Thread.Sleep(40);
                var inputs = new[] { MouseInput(0x0002), MouseInput(0x0004) };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
                Thread.Sleep(60);
            }
            finally
            {
                SetCursorPos(original.X, original.Y);
            }
        }

        public static void PressKey(ushort virtualKey)
        {
            var inputs = new[] { KeyInput(virtualKey, 0, 0), KeyInput(virtualKey, 0, 0x0002) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(40);
        }

        public static void SelectAll()
        {
            var inputs = new[] { KeyInput(0x11, 0, 0), KeyInput(0x41, 0, 0), KeyInput(0x41, 0, 0x0002), KeyInput(0x11, 0, 0x0002) };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(40);
        }

        public static void Type(string text)
        {
            foreach (var ch in text)
            {
                var inputs = new[] { KeyInput(0, ch, 0x0004), KeyInput(0, ch, 0x0004 | 0x0002) };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
                Thread.Sleep(15);
            }
        }

        public const ushort VkReturn = 0x0D, VkEnd = 0x23, VkBack = 0x08;

        static INPUT MouseInput(uint flags) => new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };

        static INPUT KeyInput(ushort vk, ushort scan, uint flags) =>
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        // x64 layout (the project targets x64): the union starts at offset 8.
        [StructLayout(LayoutKind.Explicit)]
        struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
