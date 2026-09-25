using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.DebugProtocol;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;
using Exception = System.Exception;
using Thread = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread;

namespace Onec.DebugAdapter.Services
{
    public class V8DebugAdapter : DebugAdapterBase, IDisposable
    {
        private CancellationToken _cancellation;
        private bool disposedValue;
        private bool _attached = false;
        private bool _ownsDebuggee;
        private readonly object _disconnectGate = new();
        private Task? _disconnectTask;
        private readonly object _endClientGate = new();
        private Task? _endClientTask;
        private IReadOnlySet<string> _clientTargetIds = new HashSet<string>();
        private int _terminatedSent;
        private volatile bool _serverLost;

        // Клиент выходит по команде сервера отладки за секунды; запас — на медленный сервер и
        // обработчики завершения конфигурации.
        private static readonly TimeSpan ClientExitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ClientCloseTimeout = TimeSpan.FromSeconds(15);
        // disconnect без предшествующего terminate: VS Code ждёт ответ две секунды, потом убивает адаптер.
        private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(1.5);

        private bool _linesStartAt1 = false;

        private readonly IDebugConfiguration _configuration;
        private readonly IMetadataProvider _metadataProvider;
        private readonly IDebugServerClient _debugServerClient;
        private readonly IDebugServerListener _debugServerListener;
        private readonly IDebugTargetsManager _debugTargetsManager;
        private readonly IStoppingManager _stoppingManager;
        private readonly IMeasureManager _measureManager;
        private readonly IDebugAdapterExtender _debugAdapterExtender;
        private readonly DebugServerProcess _debugServer;
		private readonly DebuggeeProcess _debuggee;

		public V8DebugAdapter(
            IDebugConfiguration configuration,
            IMetadataProvider metadataProvider,
            IDebugServerClient debugServerClient,
            IDebugServerListener debugServerListener,
            IDebugTargetsManager debugTargetsManager,
            IStoppingManager stoppingManager,
            IMeasureManager measureManager,
            IDebugAdapterExtender debugAdapterExtender,
            DebuggeeProcess debuggee,
            DebugServerProcess debugServer)
        {
            _configuration = configuration;
            _metadataProvider = metadataProvider;
            _debugServerClient = debugServerClient;
            _debugServerListener = debugServerListener;
            _debugTargetsManager = debugTargetsManager;
            _stoppingManager = stoppingManager;
            _measureManager = measureManager;
            _debugAdapterExtender = debugAdapterExtender;
			_debugServer = debugServer;
            _debuggee = debuggee;

            _debugServerListener.DebugServerLost += DebugServerLost;
		}

        public async Task Run(Stream input, Stream output, CancellationToken cancellationToken = default)
        {
            _cancellation = cancellationToken;
            InitializeProtocolClient(input, output);
            _cancellation.Register(Protocol.Stop);

            await Task.Run(async () =>
            {
                _debugAdapterExtender.Init(Protocol, _cancellation);

                Protocol.Run();
                Protocol.WaitForReader();

                await Disconnect();
            }, cancellationToken);
        }

        protected override void HandleInitializeRequestAsync(IRequestResponder<InitializeArguments, InitializeResponse> responder)
        {
			_linesStartAt1 = responder.Arguments.LinesStartAt1 ?? false;

            responder.SetResponse(new()
            {
                SupportsEvaluateForHovers = true,
                SupportsExceptionFilterOptions = true,
                SupportsConditionalBreakpoints = true,
                SupportsLogPoints = true,
                SupportsSetVariable = true,
                SupportsSingleThreadExecutionRequests = true,
                // Без terminate VS Code шлёт disconnect и через две секунды убивает адаптер.
                SupportsTerminateRequest = true,
                ExceptionBreakpointFilters = new()
                {
                    new()
                    {
                        Filter = "all",
                        Label = "Остановка по ошибке",
                        Description = "Остановка при возникновении исключения времени выполнения",
                        SupportsCondition = true,
                        ConditionDescription = "Искомая подстрока текста исключения"
                    }
                }
            });
        }

        protected override async void HandleLaunchRequestAsync(IRequestResponder<LaunchArguments> responder)
        {
            try
            {
                await InitLaunchAttach(responder, responder.Arguments.ConfigurationProperties, true);
            }
            catch (DebugStartException ex)
            {
                SetProtocolError(responder, ex.Message);
            }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка запуска отладки (запуск)", ex);
            }
        }

        protected override async void HandleAttachRequestAsync(IRequestResponder<AttachArguments> responder)
        {
            try
            {
                await InitLaunchAttach(responder, responder.Arguments.ConfigurationProperties, false);
            }
            catch (DebugStartException ex)
            {
                SetProtocolError(responder, ex.Message);
            }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка запуска отладки (присоединение)", ex);
            }
        }

        protected override void HandleThreadsRequestAsync(IRequestResponder<ThreadsArguments, ThreadsResponse> responder)
        {
            try
            {
                responder.SetResponse(_debugTargetsManager.GetThreads(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка при выполнение запроса подключенных предметов отладки", ex);
            }
        }

        protected override async void HandleSetBreakpointsRequestAsync(IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.SetBreakpoints(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Debug($"setBreakpoints: ошибка. {ex.Message}");
                SetProtocolError(responder, $"Ошибка при обработке запроса конфигурации точек останова: {ex.Message}", ex);
            }
        }

        protected override async void HandleSetExceptionBreakpointsRequestAsync(IRequestResponder<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.SetExceptionBreakpoints(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка при обработке запроса конфигурации останова по исключению", ex);
            }
        }

        protected override async void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.GetCallStack(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, $"Ошибка при получении стека вызовов потока {responder.Arguments.ThreadId}", ex);
            }
        }

        protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
        {
            try
            {
                responder.SetResponse(_stoppingManager.GetScopes(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, $"Ошибка при получении областей переменных потока", ex);
            }
        }

        protected override async void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.GetVariables(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, $"Ошибка при получении переменных", ex);
            }
        }

        protected override async void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.SetVariable(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, $"Ошибка при изменении значения переменной: {ex.Message}", ex);
            }
        }

        protected override async void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
        {
            try
            {
                responder.SetResponse(await _stoppingManager.Evaluate(responder.Arguments));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProtocolError(responder, $"Ошибка при вычислении выражения", ex);
            }
        }

        protected override async void HandleContinueRequestAsync(IRequestResponder<ContinueArguments, ContinueResponse> responder)
            => await SendStepEvent(responder, responder.Arguments.ThreadId, DebugStepAction.Continue, responder.Arguments.SingleThread == true, () =>
            {
                responder.SetResponse(new ContinueResponse() { AllThreadsContinued = responder.Arguments.SingleThread != true });
            });

        protected override async void HandleNextRequestAsync(IRequestResponder<NextArguments> responder)
            => await SendStepEvent(responder, responder.Arguments.ThreadId, DebugStepAction.Step, responder.Arguments.SingleThread == true);

        protected override async void HandleStepInRequestAsync(IRequestResponder<StepInArguments> responder)
            => await SendStepEvent(responder, responder.Arguments.ThreadId, DebugStepAction.StepIn, responder.Arguments.SingleThread == true);

        protected override async void HandleStepOutRequestAsync(IRequestResponder<StepOutArguments> responder)
            => await SendStepEvent(responder, responder.Arguments.ThreadId, DebugStepAction.StepOut, responder.Arguments.SingleThread == true);

        protected override void HandleTerminateRequestAsync(IRequestResponder<TerminateArguments> responder)
        {
            // Ответ сразу: событие terminated уйдёт, когда клиент закроется сам.
            responder.SetResponse(new TerminateResponse());
            _ = EndClient();
        }

        protected override async void HandleDisconnectRequestAsync(IRequestResponder<DisconnectArguments> responder)
        {
            try
            {
                await Disconnect();
                responder.SetResponse(new DisconnectResponse());
            }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка завершения отладки", ex);
            }
        }

        // Запрос без обработчика базовый класс отклоняет исключением NotImplementedException, а на
        // любом исключении, кроме ProtocolException, библиотека останавливает протокол и адаптер
        // завершается. VS Code шлёт такие запросы и без заявленной поддержки: source, pause.
        protected override ResponseBody HandleProtocolRequest(string requestType, object requestArgs)
        {
            try
            {
                return base.HandleProtocolRequest(requestType, requestArgs);
            }
            catch (NotImplementedException)
            {
                Log.Debug($"запрос {requestType} не поддерживается");
                throw new ProtocolException($"Команда отладки «{requestType}» не поддерживается");
            }
        }

        private async Task InitLaunchAttach(IRequestResponder responder, Dictionary<string, JToken> configurationArgs, bool launch)
        {
            await _configuration.Init(configurationArgs);
            Log.Init(Protocol, _configuration.DiagnosticLogging);
            Log.Debug($"старт отладки ({(launch ? "launch" : "attach")}); файловая ИБ={_configuration.IsFileInfoBase}; свой сервер отладки={_configuration.OwnsDebugServer}");

            if (_configuration.OwnsDebugServer)
                await _debugServer.Run(Protocol);

            try
            {
                await _debugServerClient.Test(_cancellation);
            }
            catch (Exception ex) when (!_configuration.OwnsDebugServer && ex is not OperationCanceledException)
            {
                Log.Debug($"сервер отладки не отвечает: {ex.Message}");
                throw DebugStartException.DebugServerUnavailable(
                    _configuration.DebugServerHost,
                    _configuration.DebugServerPort,
                    clusterService: launch && !_configuration.IsFileInfoBase);
            }
            Log.Debug($"сервер отладки: {_configuration.DebugServerHost}:{_configuration.DebugServerPort}");

            var response = await _debugServerClient.AttachDebugUI(_configuration.CreateRequest<RdbgAttachDebugUiRequest>(i =>
            {
                i.Options = new DebuggerOptions()
                {
                    ForegroundAbility = true
                };
            }));

			_attached = true;

            switch (response!.Result)
            {
                case AttachDebugUiResult.Unknown:
					SetProtocolError(responder, "Неизвестная ошибка при подключении к серверу отладки");
                    break;
                case AttachDebugUiResult.IbInDebug:
					SetProtocolError(responder, "Информационная база уже отлаживается");
                    break;
                case AttachDebugUiResult.NotRegistered:
					SetProtocolError(responder, "Не удалось подключиться к серверу отладки");
                    break;
                case AttachDebugUiResult.CredentialsRequired:
                case AttachDebugUiResult.FullCredentialsRequired:
					SetProtocolError(responder, "Ошибка аутентификации на сервере отладки");
                    break;
                default:
					// Клиент только после успешного подключения отладчика: иначе он остался бы
					// в режиме отладки без отладчика, а VS Code показал бы ошибку запуска.
					if (launch)
					{
						// Снимок до запуска: по нему среди предметов базы узнаётся сеанс своего клиента.
						await _debugTargetsManager.RememberTargetsBeforeClient();
						_debuggee.Exited += DebuggeeExited;
						_debuggee.Run();
						_ownsDebuggee = true;
					}

					await _metadataProvider.Init(Protocol, _cancellation);
                    _debugServerListener.Run(Protocol, _cancellation);
					await _debugTargetsManager.Run(Protocol, _cancellation);
                    _stoppingManager.Run(Protocol, _cancellation);

					responder.SetResponse(launch ? new LaunchResponse() : new AttachResponse());
					Protocol.SendEvent(new InitializedEvent());

					break;
            };
        }

        private void DebugServerLost(object? sender, string reason)
        {
            _serverLost = true;
            Protocol.SendError(reason);
            SendTerminated();
        }

        private Task Disconnect()
        {
            lock (_disconnectGate)
                return _disconnectTask ??= DisconnectCore();
        }

        private async Task DisconnectCore()
        {
            // Потерянный сервер отладки отладчика уже не знает: отключать на нём нечего.
            if (!_attached || _serverLost)
            {
                _debugServerListener.Stop();
                _attached = false;
                return;
            }

            // Обычно клиент уже закрыт запросом terminate. Иначе — повторный «Стоп» или закрытие
            // окна VS Code: команду завершения отправляем, выхода клиента не ждём.
            if (_ownsDebuggee && !_debuggee.HasExited)
                await Task.WhenAny(EndClient(), Task.Delay(DisconnectBudget));

            // Остановленный предмет ждёт команду. detachDebugUI её не шлёт, и сеанс на сервере остаётся стоять.
            // Чужие сеансы продолжают работу, свой завершается командой сервера отладки.
            await _stoppingManager.ResumeStopped(_clientTargetIds);

            _debugServerListener.Stop();
            _attached = false;

            await _measureManager.DisableOnDisconnect();
            await _debugServerClient.DetachDebugUI(_configuration.CreateRequest<RdbgDetachDebugUiRequest>());
        }

        private Task EndClient()
        {
            lock (_endClientGate)
                return _endClientTask ??= EndClientCore();
        }

        /// <summary>
        /// Закрывает запущенный клиент так же, как «Завершить отладку» в конфигураторе:
        /// команда сервера отладки завершает предметы своего сеанса, и клиент выходит сам.
        /// Убитый клиент оставил бы в кластере спящий сеанс, поэтому клиент не убивается.
        /// </summary>
        private async Task EndClientCore()
        {
            try
            {
                if (!_ownsDebuggee || !_attached || _debuggee.HasExited)
                    return;

                var targets = await _debugTargetsManager.ClientSessionTargets();
                _clientTargetIds = targets.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var terminated = targets.Count > 0 && await TerminateTargets(targets);
                if (!terminated)
                    _debuggee.RequestClose();

                if (!await _debuggee.WaitForExit(ClientExitTimeout) && terminated && _debuggee.RequestClose())
                    await _debuggee.WaitForExit(ClientCloseTimeout);

                if (!_debuggee.HasExited)
                {
                    Log.Debug("клиент 1С не закрылся; оставлен открытым, чтобы не оборвать сеанс");
                    Protocol.SendError("Клиент 1С не закрылся. Закройте его окно.");
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"завершение клиента 1С: {ex.Message}");
            }
            finally
            {
                SendTerminated();
            }
        }

        private void DebuggeeExited(int? exitCode)
        {
            if (exitCode is int code && code != 0)
                Log.Debug($"клиент 1С завершился с кодом {code}");

            SendTerminated();
        }

        private void SendTerminated()
        {
            if (Interlocked.Exchange(ref _terminatedSent, 1) != 0)
                return;

            try
            {
                Protocol.SendEvent(new TerminatedEvent());
            }
            catch (Exception ex)
            {
                // Канал уже закрыт: VS Code завершил сессию сам.
                Log.Debug($"событие terminated не отправлено: {ex.Message}");
            }
        }

        /// <summary>true, если сервер отладки принял команду.</summary>
        private async Task<bool> TerminateTargets(IReadOnlyList<DebugTargetId> targets)
        {
            var request = _configuration.CreateRequest<RdbgTerminateRequest>();
            foreach (var target in targets)
                request.TargetId.Add(target);

            // Идентификатор нужен целиком: одного uuid сервер отладки не принимает.
            Log.Debug($"завершение предметов отладки: {string.Join(", ", targets.Select(t => $"{t.TargetType} сеанс {t.SeanceNo}"))}");
            try
            {
                await _debugServerClient.Terminate(request);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug($"завершение предметов отладки: {ex.Message}");
                return false;
            }
        }

        private async Task SendStepEvent<T>(T responder, int threadId, DebugStepAction action, bool singleThread, Action? successAction = null) where T : IRequestResponder
        {
            try
            {
                var request = _configuration.CreateRequest<RdbgStepRequest>();
                request.TargetId = _debugTargetsManager.GetTargetId(threadId).ToLight();
                request.Action = action;

                // Отметка до отправки: запрос значений может прийти, пока шаг ещё в пути.
                _stoppingManager.ThreadResumed(threadId);
                var response = await _debugServerClient.Step(request, _cancellation);

                if (response?.ItemSpecified == true)
                    foreach (var item in response!.Item)
                    {
                        if (_debugTargetsManager.DebugTargetAttached(item.TargetId))
                        {
                            var itemThreadId = _debugTargetsManager.GetThreadId(item.TargetId);

                            switch (item.State)
                            {
                                case DbgTargetState.Worked:
                                    _stoppingManager.ThreadResumed(itemThreadId);
                                    Protocol.SendEvent(new ContinuedEvent()
                                    {
                                        ThreadId = itemThreadId,
                                        AllThreadsContinued = false
                                    });
                                    break;
                                default:
                                    break;
                            };
                        }
                    }

                successAction?.Invoke();
            }
            catch (Exception ex)
            {
                SetProtocolError(responder, "Ошибка отправки события шага отладки", ex);
            }
        }

        private static void SetProtocolError(IRequestResponder responder, string message)
            => responder.SetError(new ProtocolException(message));

		private void SetProtocolError(IRequestResponder responder, string message, Exception exception)
        {
			Protocol.SendError(exception);
			responder.SetError(new ProtocolException(message));
		}

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _debugServerClient.Dispose();
                }

                disposedValue = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
