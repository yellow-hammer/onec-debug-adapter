using System.IO.Pipes;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Значения переменных сервер отдаёт только остановленному предмету отладки: на ходу
    /// он отвечает «Выполнение вычислений возможно только в остановленном предмете отладки».
    /// Пока поток отмечен как выполняющийся, значения не запрашиваются, а отказ на запрос,
    /// ушедший до продолжения, не считается ошибкой сессии.
    /// </summary>
    public class ResumedThreadTests : IDisposable
    {
        private readonly AnonymousPipeServerStream _input = new(PipeDirection.In);
        private readonly MemoryStream _output = new();
        private readonly DebugProtocolClient _client;
        private readonly FakeDebugServerListener _listener = new();
        private readonly RefusingClient _server = new();
        private readonly StoppingManager _manager;

        public ResumedThreadTests()
        {
            _client = new DebugProtocolClient(_input, _output);
            _client.Run();

            _manager = new StoppingManager(
                new FakeDebugConfiguration(string.Empty, []), new UnusedMetadata(), _server, _listener, new FakeDebugTargetsManager());
            _manager.Run(_client, CancellationToken.None);
        }

        [Fact]
        public async Task ПослеПродолженияЗначенияНеЗапрашиваются()
        {
            var reference = StopAndTakeLocalsReference();
            _manager.ThreadResumed(0);

            var response = await _manager.GetVariables(new VariablesArguments { VariablesReference = reference });

            Assert.Empty(response.Variables ?? []);
            Assert.False(_server.Asked, "сервер спрашивать не о чем: выполнение продолжено");
        }

        /// <summary>Запрос ушёл до продолжения: отказ приходит уже на ходу и ошибкой не считается.</summary>
        [Fact]
        public async Task ОтказПослеПродолженияНеОшибка()
        {
            var reference = StopAndTakeLocalsReference();
            _server.OnAsk = () => _manager.ThreadResumed(0);

            var response = await _manager.GetVariables(new VariablesArguments { VariablesReference = reference });

            Assert.Empty(response.Variables ?? []);
            Assert.True(_server.Asked);
        }

        /// <summary>Остановленный предмет отладки: отказ сервера скрывать нельзя.</summary>
        [Fact]
        public async Task ОтказНаОстановленномОстаётсяОшибкой()
        {
            var reference = StopAndTakeLocalsReference();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _manager.GetVariables(new VariablesArguments { VariablesReference = reference }));
        }

        /// <summary>Точка с выводом сообщения продолжает выполнение, а не останавливает его.</summary>
        [Fact]
        public async Task ТочкаСВыводомСообщенияНеСнимаетОтметку()
        {
            var reference = StopAndTakeLocalsReference();
            _manager.ThreadResumed(0);

            _listener.RaiseCallStackFormed(Stop(messageOnly: true));
            var response = await _manager.GetVariables(new VariablesArguments { VariablesReference = reference });

            Assert.False(_server.Asked, "предмет отладки идёт дальше, значений у него нет");
        }

        [Fact]
        public async Task ОстановСнимаетОтметку()
        {
            var reference = StopAndTakeLocalsReference();
            _manager.ThreadResumed(0);

            _listener.RaiseCallStackFormed(Stop());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _manager.GetVariables(new VariablesArguments { VariablesReference = reference }));
        }

        /// <summary>Останов, стек и область «Локальные»: ссылку на неё VS Code шлёт в запросе значений.</summary>
        private int StopAndTakeLocalsReference()
        {
            _listener.RaiseCallStackFormed(Stop());

            var stack = _manager.GetCallStack(new StackTraceArguments { ThreadId = 0 }).Result;
            var frameId = stack.StackFrames[0].Id;

            return _manager.GetScopes(new ScopesArguments { FrameId = frameId }).Scopes[0].VariablesReference;
        }

        private static CallStackFormedEventArgs Stop(bool messageOnly = false)
        {
            var info = new DbguiExtCmdInfoCallStackFormed { StopByBp = true, SendMessageOnly = messageOnly };
            info.CallStack.Add(new StackItemViewInfoData { LineNo = 4, Presentation = "Процедура"u8.ToArray() });

            return new CallStackFormedEventArgs(info);
        }

        public void Dispose()
        {
            _client.Stop();
            _input.Dispose();
            _output.Dispose();
        }

        /// <summary>Сервер отладки, который отказывает на любые вычисления.</summary>
        private sealed class RefusingClient : FakeDebugServerClient
        {
            public bool Asked { get; private set; }
            public Action? OnAsk { get; set; }

            public override Task<RdbgEvalLocalVariablesResponse?> EvalLocalVariables(
                RdbgEvalLocalVariablesRequest request, CancellationToken cancellationToken = default)
            {
                Asked = true;
                OnAsk?.Invoke();

                throw new InvalidOperationException(
                    "evalLocalVariables: 400 Bad request. Выполнение вычислений возможно только в остановленном предмете отладки");
            }
        }

        /// <summary>Кэш модулей в этих проверках не нужен: до путей дело не доходит.</summary>
        private sealed class UnusedMetadata : IMetadataProvider
        {
            public Task Init(DebugProtocolClient client, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default) => null;
            public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info) => false;
            public string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info) => "";
            public string ExternalModuleUrlByPath(string path) => "";
            public string? TryModulePathByExternalUrl(string url, string propertyId) => null;
            public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info) => null;
            public IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info)
                => [];
        }
    }
}
