using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Desktop;

namespace VcomBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => BridgeApp.Run(BridgeEdition.Generic, args);
}
