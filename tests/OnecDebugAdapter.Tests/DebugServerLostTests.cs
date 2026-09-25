using System.IO.Pipes;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugProtocol;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Автономный сервер держит сервер отладки в своём процессе. Остановленный сервер не отвечает,
    /// перезапущенный отвечает на опрос кодом 400: отладчик на нём не зарегистрирован.
    /// </summary>
    public class DebugServerLostTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly DateTime Start = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Отказ400ЗавершаетОтладкуСразу()
        {
            var liveness = new DebugServerLiveness(TimeSpan.FromSeconds(10));

            var reason = liveness.Failed(new DebugServerException(400, "pingDebugUIParams: 400"), Start);

            Assert.NotNull(reason);
        }

        [Fact]
        public void НедоступныйСерверЗавершаетОтладкуПоТаймауту()
        {
            var liveness = new DebugServerLiveness(TimeSpan.FromSeconds(10));
            var refused = new InvalidOperationException("pingDebugUIParams: connection refused");

            Assert.Null(liveness.Failed(refused, Start));
            Assert.Null(liveness.Failed(refused, Start.AddSeconds(9)));
            Assert.NotNull(liveness.Failed(refused, Start.AddSeconds(10)));
        }

        [Fact]
        public void ОтветСервераСбрасываетОжидание()
        {
            var liveness = new DebugServerLiveness(TimeSpan.FromSeconds(10));
            var refused = new InvalidOperationException("pingDebugUIParams: connection refused");

            liveness.Failed(refused, Start);
            liveness.Succeeded();

            Assert.Null(liveness.Failed(refused, Start.AddSeconds(10)));
        }

        [Fact]
        public async Task ПотеряСервераЗавершаетСессию()
        {
            using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
            using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.In);
            using var adapterInput = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
            using var adapterOutput = new AnonymousPipeClientStream(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);

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
            var adapterRun = adapter.Run(adapterInput, adapterOutput);

            var host = new DebugProtocolHost(toAdapter, fromAdapter);
            var terminated = new TaskCompletionSource();
            var errors = new List<string>();
            host.EventReceived += (_, e) =>
            {
                if (e.Body is OutputEvent output)
                    lock (errors) errors.Add(output.Output);
                else if (e.Body is TerminatedEvent)
                    terminated.TrySetResult();
            };
            host.Run();
            await Task.Run(() => host.SendRequestSync(new InitializeRequest("1c-platform-tools"))).WaitAsync(Timeout);

            listener.RaiseDebugServerLost("Сервер отладки перезапущен, отладка завершена.");

            await terminated.Task.WaitAsync(Timeout);
            lock (errors)
                Assert.Contains(errors, e => e.Contains("Сервер отладки перезапущен"));

            await Task.Run(() => host.SendRequestSync(new DisconnectRequest())).WaitAsync(Timeout);

            host.Stop();
            toAdapter.Dispose();
            await adapterRun.WaitAsync(Timeout);
        }
    }
}
