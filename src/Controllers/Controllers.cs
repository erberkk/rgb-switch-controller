using System.Collections.Generic;
using System.Linq;
using PcControl.Core;

namespace PcControl.Controllers
{
    public static class DeviceRegistry
    {
        public static IEnumerable<IDeviceController> All() =>
            new IDeviceController[]
            {
                new LConnectController(),
                new GccController(),
                new TrixxController(),
                new KanaliController(),
            }.Where(c => c.IsInstalled);
    }
}
