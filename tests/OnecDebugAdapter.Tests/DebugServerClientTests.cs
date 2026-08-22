using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Onec.DebugAdapter.DebugServer;
using RestSharp;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Сообщение об отказе сервера отладки. Раньше причину показывала только команда
    /// setBreakpoints, а остальные оставляли в журнале «Request failed with status code»
    /// без имени команды и без объяснения сервера.
    /// </summary>
    public class DebugServerClientTests
    {
        [Fact]
        public async Task ОтказСерверПоясняетВСообщении()
        {
            using var server = StubServer.Answering(400, "Bad request", "<exception>Ошибка разбора XML</exception>");
            var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SetBreakpoints(new RdbgSetBreakpointsRequest()));

            Assert.Contains("setBreakpoints", error.Message);
            Assert.Contains("400", error.Message);
            Assert.Contains("Ошибка разбора XML", error.Message);
        }

        /// <summary>Имя команды нужно у каждой: по одному коду статуса не понять, что отказало.</summary>
        [Theory]
        [InlineData("setBreakOnRTE")]
        [InlineData("attachDebugUI")]
        [InlineData("getDbgTargets")]
        [InlineData("pingDebugUIParams")]
        [InlineData("step")]
        public async Task КомандаНазываетСебяВОшибке(string command)
        {
            using var server = StubServer.Answering(400, "Bad request", "<exception>отказ</exception>");
            var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(client, command));

            Assert.Contains(command, error.Message);
            Assert.Contains("отказ", error.Message);
        }

        [Fact]
        public async Task УспешныйОтветРазбираетсяВТип()
        {
            using var server = StubServer.Answering(200, "OK", Response(new RdbgPingDebugUiResponse()));
            var client = ClientFor(server);

            var response = await client.PingDebugUiParams("dbgui");

            Assert.NotNull(response);
        }

        [Fact]
        public async Task УспешныйОтветБезТелаНеОшибка()
        {
            using var server = StubServer.Answering(200, "OK", "");
            var client = ClientFor(server);

            await client.SetBreakpoints(new RdbgSetBreakpointsRequest());
        }

        /// <summary>Сервер не поднялся: в сообщении всё равно должно быть имя команды.</summary>
        [Fact]
        public async Task НедоступныйСерверНазванПоКоманде()
        {
            var client = new DebugServerClient(ConfigurationFor(FreePort()));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SetBreakpoints(new RdbgSetBreakpointsRequest()));

            Assert.Contains("setBreakpoints", error.Message);
        }

        /// <summary>Отмена сессии закрывает запросы на полпути: это не отказ сервера.</summary>
        [Fact]
        public void ОтменаНеВыглядитОшибкойСервера()
        {
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var response = new RestResponse(new RestRequest())
            {
                StatusCode = HttpStatusCode.BadRequest,
                StatusDescription = "Bad request",
                Content = "<exception>отказ</exception>"
            };

            Assert.Throws<OperationCanceledException>(
                () => DebugServerClient.Ensure("setBreakpoints", response, cancelled.Token));
        }

        [Fact]
        public void УспешныйОтветОшибкиНеДаёт()
        {
            var response = new RestResponse(new RestRequest())
            {
                StatusCode = HttpStatusCode.OK,
                ResponseStatus = ResponseStatus.Completed
            };

            DebugServerClient.Ensure("setBreakpoints", response, CancellationToken.None);
        }

        private static Task Call(IDebugServerClient client, string command) => command switch
        {
            "setBreakOnRTE" => client.SetBreakOnRTE(new RdbgSetRunTimeErrorProcessingRequest()),
            "attachDebugUI" => client.AttachDebugUI(new RdbgAttachDebugUiRequest()),
            "getDbgTargets" => client.GetDbgTargets(new RdbgsGetDbgTargetsRequest()),
            "pingDebugUIParams" => client.PingDebugUiParams("dbgui"),
            "step" => client.Step(new RdbgStepRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "команда не заведена в тесте")
        };

        private static DebugServerClient ClientFor(StubServer server) => new(ConfigurationFor(server.Port));

        private static FakeDebugConfiguration ConfigurationFor(int port)
            => new(rootProject: string.Empty, extensions: [], debugServerPort: port);

        /// <summary>Тело успешного ответа в том виде, в каком его разбирает RequestSerializer.</summary>
        private static string Response<T>(T value)
        {
            var root = new XmlRootAttribute("response") { Namespace = "http://v8.1c.ru/8.3/debugger/debugBaseData" };
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                new XmlSerializer(typeof(T), root).Serialize(writer, value);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Сервер отладки на localhost: отвечает одним и тем же на любую команду.</summary>
        private sealed class StubServer : IDisposable
        {
            private readonly HttpListener _listener = new();

            public int Port { get; }

            private StubServer(int port, int status, string description, string body)
            {
                Port = port;
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();

                _ = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context;
                        try { context = await _listener.GetContextAsync(); }
                        catch (HttpListenerException) { return; }
                        catch (ObjectDisposedException) { return; }

                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.StatusCode = status;
                        context.Response.StatusDescription = description;
                        context.Response.ContentType = "text/xml; charset=utf-8";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                });
            }

            public static StubServer Answering(int status, string description, string body)
                => new(FreePort(), status, description, body);

            public void Dispose()
            {
                _listener.Stop();
                ((IDisposable)_listener).Dispose();
            }
        }
    }
}
