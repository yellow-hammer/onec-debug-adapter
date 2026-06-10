using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Onec.DebugAdapter.Services
{
    public class ConsoleDebugAdapterService : BackgroundService
    {
        private readonly ILogger<ConsoleDebugAdapterService> _logger;
        private readonly IServiceProvider _services;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;

        public ConsoleDebugAdapterService(IServiceProvider services, IHostApplicationLifetime hostApplicationLifetime, ILogger<ConsoleDebugAdapterService> logger)
        {
            _hostApplicationLifetime = hostApplicationLifetime;
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Сервисы сессии — Scoped (см. Program), адаптер берём из scope.
                using var scope = _services.CreateScope();
                var debugAdapter = scope.ServiceProvider.GetRequiredService<V8DebugAdapter>();

                await debugAdapter.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), stoppingToken);

                if (!_hostApplicationLifetime.ApplicationStopping.IsCancellationRequested)
                    _hostApplicationLifetime.StopApplication();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }
    }
}
