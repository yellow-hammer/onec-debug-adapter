using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Сервер отладки: запоминает последний запрос точек, остальные команды не ожидаются.</summary>
    internal class FakeDebugServerClient : IDebugServerClient
    {
        public RdbgSetBreakpointsRequest? LastRequest { get; private set; }

        public Task SetBreakpoints(RdbgSetBreakpointsRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task<RdbgStepResponse?> Step(RdbgStepRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<RdbgStepResponse?>(null);

        public virtual Task<RdbgEvalLocalVariablesResponse?> EvalLocalVariables(RdbgEvalLocalVariablesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
        public Task ClearBreakOnNextStatement(RdbgSetBreamOnNextStatementRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgAttachDebugUiResponse?> AttachDebugUI(RdbgAttachDebugUiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgAttachDetachDbgTargetResponse?> AttachDetachDbgTargets(RdbgAttachDetachDebugTargetsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgDetachDebugUiResponse?> DetachDebugUI(RdbgDetachDebugUiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgEvalExprResponse?> EvalExpr(RdbgEvalExprRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ModifyValue(RdbgModifyValueRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgGetCallStackResponse?> GetCallStack(RdbgGetCallStackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgsGetDbgTargetsResponse?> GetDbgTargets(RdbgsGetDbgTargetsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InitSettings(RdbgSetInitialDebugSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdbgPingDebugUiResponse?> PingDebugUiParams(string dbgUi, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAutoAttachSettings(RdbgSetAutoAttachSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetBreakOnRTE(RdbgSetRunTimeErrorProcessingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMeasureMode(RdbgSetMeasureModeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Test(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>События сервера отладки: тест поднимает их сам.</summary>
    internal sealed class FakeDebugServerListener : IDebugServerListener
    {
#pragma warning disable CS0067
        public event EventHandler<CallStackFormedEventArgs>? CallStackFormed;
        public event EventHandler<DebugTargetEventArgs>? DebugTargetEvent;
        public event EventHandler<ExpressionEvaluatedEventArgs>? ExpressionEvaluated;
        public event EventHandler<RuntimeExceptionArgs>? RuntimeException;
        public event EventHandler<CorrectedBreakpointsArgs>? CorrectedBreakpoints;
        public event EventHandler<SetForegroundHelperArgs>? SetForegroundHelper;
        public event EventHandler<ForegroundHelperRequestArgs>? ForegroundHelperRequested;
        public event EventHandler<ProcessForegroundHelperArgs>? ProcessForegroundHelper;
        public event EventHandler<ShowMetadataObjectArgs>? ShowMetadataObject;
        public event EventHandler<MeasureResultsEventArgs>? MeasureResults;
#pragma warning restore CS0067

        public void RaiseCallStackFormed(CallStackFormedEventArgs args) => CallStackFormed?.Invoke(this, args);

        public void Run(DebugProtocolClient debugProtocolClient, CancellationToken cancellationToken) { }
        public void Stop() { }
    }

    internal sealed class FakeDebugTargetsManager : IDebugTargetsManager
    {
        public Task Run(DebugProtocolClient client, CancellationToken cancellationToken) => Task.CompletedTask;
        public DebugTargetId GetTargetId(int threadId) => new();
        public DebugTargetId[] GetAttachedDebugTargets() => [];
        public Task<DebugTargetId[]> GetDebugTargets() => Task.FromResult<DebugTargetId[]>([]);
        public Task SetAutoAttachTargetTypes(List<DebugTargetType> types) => Task.CompletedTask;
        public List<DebugTargetType> GetAutoAttachTargetTypes() => [];
        public Task AttachDebugTargets(List<DebugTargetId> debugTargets) => Task.CompletedTask;
        public Task DetachDebugTargets(List<DebugTargetIdLight> debugTargets, bool sendDetachRequest) => Task.CompletedTask;
        public ThreadsResponse GetThreads(ThreadsArguments args) => throw new NotSupportedException();
        public int GetThreadId(DebugTargetIdLight debugTargetId) => 0;
        public bool DebugTargetAttached(DebugTargetIdLight debugTarget) => false;
    }
}
