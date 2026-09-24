using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RgbSwitch.Core
{
    public enum DeviceState { Unknown, Ready, NeedsSetup, NotRunning, Error }

    public sealed class ProbeResult
    {
        public DeviceState State;
        public string Detail;
        public bool? LightsOn;

        public static ProbeResult Ready(string detail, bool? lightsOn = null) =>
            new ProbeResult { State = DeviceState.Ready, Detail = detail, LightsOn = lightsOn };

        public static ProbeResult Of(DeviceState state, string detail) =>
            new ProbeResult { State = state, Detail = detail };
    }

    public interface IDeviceController
    {
        string Id { get; }
        string Title { get; }
        string Source { get; }
        string Glyph { get; }

        // False when the vendor app is not installed; such devices are left out entirely.
        bool IsInstalled { get; }

        Task<ProbeResult> ProbeAsync(CancellationToken ct);

        // Returns the snapshot needed to restore the lights later. When the device is
        // already dark, implementations return `previous` so an earlier snapshot survives.
        Task<Dictionary<string, object>> TurnOffAsync(Dictionary<string, object> previous, CancellationToken ct);

        Task TurnOnAsync(Dictionary<string, object> snapshot, CancellationToken ct);
    }
}
