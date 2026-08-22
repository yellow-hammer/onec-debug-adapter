using Onec.DebugAdapter.Services;
using RestSharp;

namespace Onec.DebugAdapter.DebugServer
{
    public class DebugServerClient : IDebugServerClient, IDisposable
    {
		private readonly TaskCompletionSource _tcs = new();

		// Без ThrowOnAnyError: с флагом RestSharp бросает исключение раньше, чем читается
		// тело ответа, и объяснение сервера теряется. Ошибку разбирает Ensure.
		private RestClient _client = null!;

        public DebugServerClient(IDebugConfiguration configuration)
        {
	        configuration.Initialization.ContinueWith(c =>
            {
				var options = new RestClientOptions($"http://{configuration.DebugServerHost}:{configuration.DebugServerPort}/e1crdbg")
				{
					UserAgent = "1CV8"
				};

				_client = new RestClient(options, configureSerialization: s =>
				{
					s.UseSerializer<RequestSerializer>();
				})
				{
					AcceptedContentTypes = new string[] { ContentType.Xml }
				};

                _tcs.SetResult();
			});
        }

        public Task Test(CancellationToken cancellationToken = default)
            => Send("test", Rdbg("rdbgTest", "test"), cancellationToken);

        public Task ClearBreakOnNextStatement(RdbgSetBreamOnNextStatementRequest request, CancellationToken cancellationToken = default)
            => Send("clearBreakOnNextStatement", Body("clearBreakOnNextStatement", request), cancellationToken);

        public Task<RdbgAttachDebugUiResponse?> AttachDebugUI(RdbgAttachDebugUiRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgAttachDebugUiResponse>("attachDebugUI", Body("attachDebugUI", request), cancellationToken);

        public Task InitSettings(RdbgSetInitialDebugSettingsRequest request, CancellationToken cancellationToken = default)
            => Send("initSettings", Body("initSettings", request), cancellationToken);

        public Task<RdbgDetachDebugUiResponse?> DetachDebugUI(RdbgDetachDebugUiRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgDetachDebugUiResponse>("detachDebugUI", Body("detachDebugUI", request), cancellationToken);

        public Task<RdbgsGetDbgTargetsResponse?> GetDbgTargets(RdbgsGetDbgTargetsRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgsGetDbgTargetsResponse>("getDbgTargets", Body("getDbgTargets", request), cancellationToken);

        public Task SetBreakpoints(RdbgSetBreakpointsRequest request, CancellationToken cancellationToken = default)
            => Send("setBreakpoints", Body("setBreakpoints", request), cancellationToken);

        public Task SetMeasureMode(RdbgSetMeasureModeRequest request, CancellationToken cancellationToken = default)
            => Send("setMeasureMode", Body("setMeasureMode", request), cancellationToken);

        public Task SetBreakOnRTE(RdbgSetRunTimeErrorProcessingRequest request, CancellationToken cancellationToken = default)
            => Send("setBreakOnRTE", Body("setBreakOnRTE", request), cancellationToken);

        public Task<RdbgGetCallStackResponse?> GetCallStack(RdbgGetCallStackRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgGetCallStackResponse>("getCallStack", Body("getCallStack", request), cancellationToken);

        public Task<RdbgPingDebugUiResponse?> PingDebugUiParams(string dbgUi, CancellationToken cancellationToken = default)
        {
            var request = Rdbg("rdbg", "pingDebugUIParams");
            request.AddQueryParameter("dbgui", dbgUi);

            return Send<RdbgPingDebugUiResponse>("pingDebugUIParams", request, cancellationToken);
        }

        public Task SetAutoAttachSettings(RdbgSetAutoAttachSettingsRequest request, CancellationToken cancellationToken = default)
            => Send("setAutoAttachSettings", Body("setAutoAttachSettings", request), cancellationToken);

        public Task<RdbgAttachDetachDbgTargetResponse?> AttachDetachDbgTargets(RdbgAttachDetachDebugTargetsRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgAttachDetachDbgTargetResponse>("attachDetachDbgTargets", Body("attachDetachDbgTargets", request), cancellationToken);

        public Task<RdbgEvalLocalVariablesResponse?> EvalLocalVariables(RdbgEvalLocalVariablesRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgEvalLocalVariablesResponse>("evalLocalVariables", Body("evalLocalVariables", request), cancellationToken);

        public Task<RdbgEvalExprResponse?> EvalExpr(RdbgEvalExprRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgEvalExprResponse>("evalExpr", Body("evalExpr", request), cancellationToken);

        public Task ModifyValue(RdbgModifyValueRequest request, CancellationToken cancellationToken = default)
            => Send("modifyValue", Body("modifyValue", request), cancellationToken);

        public Task<RdbgStepResponse?> Step(RdbgStepRequest request, CancellationToken cancellationToken = default)
            => Send<RdbgStepResponse>("step", Body("step", request), cancellationToken);

        public void Dispose()
        {
            _client?.Dispose();
            GC.SuppressFinalize(this);
        }

        private static RestRequest Rdbg(string resource, string command)
        {
            var request = new RestRequest(resource);
            request.AddQueryParameter("cmd", command);
            return request;
        }

        private static RestRequest Body(string command, object body)
        {
            var request = Rdbg("rdbg", command);
            request.AddXmlBody(body);
            return request;
        }

        private async Task Send(string command, RestRequest request, CancellationToken cancellationToken)
        {
            await WaitInitialized();
            Ensure(command, await _client.ExecutePostAsync(request, cancellationToken), cancellationToken);
        }

        private async Task<T?> Send<T>(string command, RestRequest request, CancellationToken cancellationToken)
        {
            await WaitInitialized();

            var response = await _client.ExecutePostAsync<T>(request, cancellationToken);
            Ensure(command, response, cancellationToken);

            return response.Data;
        }

        /// <summary>
        /// Причину отказа сервер объясняет в теле ответа, поэтому в сообщение идут и команда,
        /// и статус, и тело. Без этого в журнал попадает голое «Request failed with status code».
        /// </summary>
        internal static void Ensure(string command, RestResponse response, CancellationToken cancellationToken)
        {
            // Отмена сессии закрывает запросы на полпути: это не отказ сервера.
            cancellationToken.ThrowIfCancellationRequested();

            // Код считаем сами: IsSuccessStatusCode у RestSharp заполняется при разборе ответа,
            // а не выводится из статуса.
            var status = (int)response.StatusCode;
            if (status != 0 && (status < 200 || status > 299))
                throw new InvalidOperationException(
                    $"{command}: {status} {response.StatusDescription}. {response.Content}".Trim());

            if (response.ErrorException != null)
                throw new InvalidOperationException($"{command}: {response.ErrorException.Message}", response.ErrorException);
        }

        private async Task WaitInitialized()
            => await _tcs.Task;
    }
}
