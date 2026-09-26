using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RgbSwitch.Core
{
    public sealed class DeviceOutcome
    {
        public IDeviceController Controller;
        public Exception Error;
        public bool Ok => Error == null;
    }

    public sealed class LightingService
    {
        // Named so the tray app and a `--toggle` shortcut never drive the vendor apps at the same time.
        static readonly Semaphore CrossProcessGate = new Semaphore(1, 1, @"Local\RgbSwitch.Operation");

        SavedState state = StateStore.Load();

        public LightingService(IEnumerable<IDeviceController> controllers)
        {
            Controllers = controllers.ToList();
        }

        public IReadOnlyList<IDeviceController> Controllers { get; }
        public bool LightsOff => state.LightsOff;
        public DateTime? ChangedAt => state.ChangedAt;

        public string Color => state.Color;

        public IEnumerable<IDeviceController> ColorTargets => Controllers.Where(c => c is IColorTarget);

        public void Reload() => state = StateStore.Load();

        public async Task<List<DeviceOutcome>> ApplyColorAsync(Rgb color, IEnumerable<IDeviceController> targets, CancellationToken ct)
        {
            await Task.Run(() => CrossProcessGate.WaitOne(), ct).ConfigureAwait(false);
            try
            {
                state = StateStore.Load();
                if (state.LightsOff) throw new InvalidOperationException("önce ışıkları aç");
                var outcomes = new List<DeviceOutcome>();
                foreach (var controller in targets.Where(c => c is IColorTarget))
                {
                    var outcome = new DeviceOutcome { Controller = controller };
                    try { await ((IColorTarget)controller).ApplyColorAsync(color, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { outcome.Error = e; }
                    outcomes.Add(outcome);
                }
                state.Color = color.Hex;
                StateStore.Save(state);
                return outcomes;
            }
            finally
            {
                CrossProcessGate.Release();
            }
        }

        public Task<List<DeviceOutcome>> TurnOffAsync(CancellationToken ct) => RunAsync(_ => true, ct);

        public Task<List<DeviceOutcome>> TurnOnAsync(CancellationToken ct) => RunAsync(_ => false, ct);

        public Task<List<DeviceOutcome>> ToggleAsync(CancellationToken ct) => RunAsync(current => !current.LightsOff, ct);

        async Task<List<DeviceOutcome>> RunAsync(Func<SavedState, bool> wantOff, CancellationToken ct)
        {
            await Task.Run(() => CrossProcessGate.WaitOne(), ct).ConfigureAwait(false);
            try
            {
                state = StateStore.Load();
                var off = wantOff(state);
                var outcomes = new List<DeviceOutcome>();
                foreach (var controller in Controllers)
                {
                    var outcome = new DeviceOutcome { Controller = controller };
                    try
                    {
                        state.Snapshots.TryGetValue(controller.Id, out var snapshot);
                        if (off)
                        {
                            var saved = await controller.TurnOffAsync(snapshot, ct).ConfigureAwait(false);
                            if (saved != null) state.Snapshots[controller.Id] = saved;
                        }
                        else
                        {
                            await controller.TurnOnAsync(snapshot, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { outcome.Error = e; }
                    outcomes.Add(outcome);
                }

                state.LightsOff = off;
                state.ChangedAt = DateTime.Now;
                StateStore.Save(state);
                return outcomes;
            }
            finally
            {
                CrossProcessGate.Release();
            }
        }
    }
}
