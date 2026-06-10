using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace Onec.DebugAdapter.Services
{
    public class TcpDebugAdapterService : BackgroundService
    {
        private readonly ILogger<TcpDebugAdapterService> _logger;
        private readonly IServiceProvider _services;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly int _port;
        private readonly bool _persistent;

        public TcpDebugAdapterService(IServiceProvider services, IHostApplicationLifetime hostApplicationLifetime, IConfiguration configuration, ILogger<TcpDebugAdapterService> logger)
        {
            _hostApplicationLifetime = hostApplicationLifetime;
            _services = services;
            _logger = logger;

            _port = configuration.GetValue("port", 4711);
            // --persistent=true: после завершения сессии продолжать принимать подключения.
            _persistent = configuration.GetValue("persistent", false);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation($"Starting listening for client on {_port} port" + (_persistent ? " (persistent)" : ""));
                var listener = TcpListener.Create(_port);
                listener.Start();

                do
                {
                    using var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _logger.LogInformation($"Client connected ({client.Client.RemoteEndPoint})");

                    using var stream = client.GetStream();

                    // Scope на сессию: каждое подключение получает чистое состояние сервисов.
                    using var scope = _services.CreateScope();
                    var debugAdapter = scope.ServiceProvider.GetRequiredService<V8DebugAdapter>();

                    try
                    {
                        await debugAdapter.Run(stream, stream, stoppingToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Ошибка сессии не должна останавливать приём следующих подключений.
                        _logger.LogError(ex.Message);
                    }

                    _logger.LogInformation("Client session finished");
                } while (_persistent && !stoppingToken.IsCancellationRequested);

                if (!_hostApplicationLifetime.ApplicationStopping.IsCancellationRequested)
                    _hostApplicationLifetime.StopApplication();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }

            _logger.LogInformation($"Stopping adapter host");
        }
    }
}
