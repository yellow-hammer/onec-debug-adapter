using System.IO.Pipes;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugProtocol;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// VS Code шлёт запросы и без заявленной поддержки: source — для поля ввода консоли отладки
    /// при первом её открытии, pause — по кнопке «Пауза». Отказ на такой запрос сессию не обрывает.
    /// </summary>
    public class UnsupportedRequestTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly AnonymousPipeServerStream _toAdapter = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromAdapter = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _adapterInput;
        private readonly AnonymousPipeClientStream _adapterOutput;
        private readonly Task _adapterRun;
        private readonly DebugProtocolHost _host;

        public UnsupportedRequestTests()
        {
            _adapterInput = new(PipeDirection.In, _toAdapter.ClientSafePipeHandle);
            _adapterOutput = new(PipeDirection.Out, _fromAdapter.ClientSafePipeHandle);

            var configuration = new FakeDebugConfiguration(string.Empty, []);
            var metadata = new UnusedMetadata();
            var server = new FakeDebugServerClient();
            var listener = new FakeDebugServerListener();
            var targets = new FakeDebugTargetsManager();
            var measure = new MeasureManager(configuration, server, listener, metadata);
            var adapter = new V8DebugAdapter(
                configuration, metadata, server, listener, targets,
                new StoppingManager(configuration, metadata, server, listener, targets),
                measure,
                new DebugAdapterExtender(targets, listener, measure),
                new DebuggeeProcess(configuration),
                new DebugServerProcess(configuration));
            _adapterRun = adapter.Run(_adapterInput, _adapterOutput);

            _host = new DebugProtocolHost(_toAdapter, _fromAdapter);
            _host.Run();
        }

        [Fact]
        public async Task ЗапросИсходникаКонсолиОтладкиНеОбрываетСессию()
        {
            var request = new SourceRequest(0) { Args = { Source = new Source { Path = "replinput" } } };

            await Assert.ThrowsAsync<ProtocolException>(() => Send(() => _host.SendRequestSync(request)));
            await AssertAlive();
        }

        [Fact]
        public async Task ПаузаНеОбрываетСессию()
        {
            await Assert.ThrowsAsync<ProtocolException>(() => Send(() => _host.SendRequestSync(new PauseRequest(1))));
            await AssertAlive();
        }

        private async Task AssertAlive()
        {
            var response = await Send(() => _host.SendRequestSync(new InitializeRequest("1c-platform-tools")));

            Assert.True(response.SupportsTerminateRequest);
            Assert.False(_adapterRun.IsCompleted, "адаптер завершился");
        }

        /// <summary>Остановленный адаптер не отвечает: без предела ожидания тест бы завис.</summary>
        private static Task Send(Action send) => Task.Run(send).WaitAsync(Timeout);

        private static Task<T> Send<T>(Func<T> send) => Task.Run(send).WaitAsync(Timeout);

        public void Dispose()
        {
            _host.Stop();
            _toAdapter.Dispose();
            _adapterRun.Wait(Timeout);
            _fromAdapter.Dispose();
            _adapterInput.Dispose();
            _adapterOutput.Dispose();
        }
    }
}
