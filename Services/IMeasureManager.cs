using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;

namespace Onec.DebugAdapter.Services
{
    public interface IMeasureManager
    {
        void Init(DebugProtocolClient client, CancellationToken cancellationToken);

        Task SetMeasureMode(bool enabled);

        Task DisableOnDisconnect();
    }
}
