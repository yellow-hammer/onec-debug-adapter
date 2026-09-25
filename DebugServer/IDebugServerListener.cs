using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;

namespace Onec.DebugAdapter.DebugServer
{
    public interface IDebugServerListener
    {
        event EventHandler<CallStackFormedEventArgs>? CallStackFormed;
        event EventHandler<DebugTargetEventArgs>? DebugTargetEvent;
        event EventHandler<ExpressionEvaluatedEventArgs>? ExpressionEvaluated;
        event EventHandler<RuntimeExceptionArgs>? RuntimeException;
        event EventHandler<CorrectedBreakpointsArgs>? CorrectedBreakpoints;
        event EventHandler<SetForegroundHelperArgs>? SetForegroundHelper;
        event EventHandler<ForegroundHelperRequestArgs>? ForegroundHelperRequested;
        event EventHandler<ProcessForegroundHelperArgs>? ProcessForegroundHelper;
        event EventHandler<ShowMetadataObjectArgs>? ShowMetadataObject;
        event EventHandler<MeasureResultsEventArgs>? MeasureResults;
        /// <summary>Отладка на сервере отладки закончилась, аргумент называет причину.</summary>
        event EventHandler<string>? DebugServerLost;

        void Run(DebugProtocolClient debugProtocolClient, CancellationToken cancellationToken);
        void Stop();
    }
}