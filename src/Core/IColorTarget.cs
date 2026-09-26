using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RgbSwitch.Core
{
    public struct Rgb
    {
        public byte R, G, B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public string Hex => $"{R:X2}{G:X2}{B:X2}";

        public static bool TryParse(string text, out Rgb color)
        {
            color = default(Rgb);
            var hex = (text ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        public override string ToString() => "#" + Hex;
    }

    // A device whose vendor app can set one static colour for all of its LEDs.
    public interface IColorTarget
    {
        Task ApplyColorAsync(Rgb color, CancellationToken ct);
    }
}
