using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PcControl.Core
{
    public sealed class MonitorInput
    {
        public int Code;
        public string Name;
    }

    public sealed class MonitorInfo
    {
        public string Model;
        public int Index;
        public List<MonitorInput> Inputs = new List<MonitorInput>();
        public int? CurrentInput;
        public bool? PoweredOn;
        public bool CanPowerOff;
    }

    // Monitor control over DDC/CI (MCCS VCP 0x60 input source, 0xD6 power mode) through dxva2,
    // the same channel the monitor's own OSD buttons map to.
    public static class Monitors
    {
        const byte VcpInput = 0x60;
        const byte VcpPower = 0xD6;
        const uint PowerOn = 0x01;
        const uint PowerOff = 0x05;

        static readonly Dictionary<int, string> InputNames = new Dictionary<int, string>
        {
            [0x01] = "VGA", [0x02] = "VGA 2", [0x03] = "DVI", [0x04] = "DVI 2",
            [0x0F] = "DP", [0x10] = "DP 2", [0x11] = "HDMI 1", [0x12] = "HDMI 2",
            [0x13] = "HDMI 3", [0x1A] = "USB-C", [0x1B] = "USB-C",
        };

        public static List<MonitorInfo> List()
        {
            var result = new List<MonitorInfo>();
            ForEachPhysical((handle, index) =>
            {
                var caps = Capabilities(handle);
                var info = new MonitorInfo
                {
                    Index = index,
                    Model = Regex.Match(caps, @"model\(([^)]*)\)").Groups[1].Value.Trim(),
                    CanPowerOff = Regex.IsMatch(caps, @"D6\([^)]*05"),
                };
                if (info.Model == "") info.Model = $"Monitör {index + 1}";
                var inputs = Regex.Match(caps, @"(?<![0-9A-Fa-f])60\(([^)]*)\)");
                foreach (var code in inputs.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = Convert.ToInt32(code, 16);
                    info.Inputs.Add(new MonitorInput { Code = value, Name = InputNames.TryGetValue(value, out var n) ? n : $"Giriş {value:X2}" });
                }
                if (GetVCPFeatureAndVCPFeatureReply(handle, VcpInput, IntPtr.Zero, out var current, out _))
                    info.CurrentInput = (int)(current & 0xFF);
                if (GetVCPFeatureAndVCPFeatureReply(handle, VcpPower, IntPtr.Zero, out var power, out _))
                    info.PoweredOn = power == PowerOn;
                result.Add(info);
            });
            return result;
        }

        public static void SetInput(int index, int code) => Set(index, VcpInput, (uint)code);

        public static void SetPower(int index, bool on) => Set(index, VcpPower, on ? PowerOn : PowerOff);

        // Puts every display to sleep the way Windows' own idle timer does; any input wakes them.
        public static void SleepAll() => SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));

        static void Set(int index, byte vcp, uint value)
        {
            var done = false;
            ForEachPhysical((handle, i) =>
            {
                if (i != index) return;
                if (!SetVCPFeature(handle, vcp, value))
                    throw new InvalidOperationException("monitör komutu kabul etmedi (DDC/CI kapalı olabilir)");
                done = true;
            });
            if (!done) throw new InvalidOperationException("monitör bulunamadı");
        }

        static void ForEachPhysical(Action<IntPtr, int> action)
        {
            var handles = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, hdc, rect, data) => { handles.Add(hMonitor); return true; }, IntPtr.Zero);
            var index = 0;
            foreach (var hMonitor in handles)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) continue;
                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical)) continue;
                try
                {
                    foreach (var p in physical) action(p.Handle, index++);
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physical);
                }
            }
        }

        static string Capabilities(IntPtr handle)
        {
            if (!GetCapabilitiesStringLength(handle, out var length) || length == 0) return "";
            var text = new StringBuilder((int)length);
            return CapabilitiesRequestAndCapabilitiesReply(handle, text, length) ? text.ToString() : "";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, StringBuilder text, uint length);
        [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint maximum);
        [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);
    }
}
