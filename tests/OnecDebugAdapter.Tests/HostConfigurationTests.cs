using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Onec.DebugAdapter.Services;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Конфигурация адаптера собирается только из аргументов командной строки.
    ///
    /// Переменные окружения процесса раньше попадали в конфигурацию целиком, поэтому чужая
    /// переменная DEBUG останавливала запуск ещё до обмена по DAP.
    /// </summary>
    [Collection("environment")]
    public class HostConfigurationTests
    {
        private static IHostedService SingleHostedService(IHost host)
            => Assert.Single(host.Services.GetServices<IHostedService>());

        [Fact]
        public void ПостороннийDEBUGНеЛомаетЗапуск()
        {
            using var env = new EnvironmentVariable("DEBUG", "release");

            using var host = Program.BuildHost([]);

            Assert.IsType<ConsoleDebugAdapterService>(SingleHostedService(host));
        }

        [Fact]
        public void ПеременнаяОкруженияНеВключаетРежимОтладки()
        {
            using var env = new EnvironmentVariable("DEBUG", "true");

            using var host = Program.BuildHost([]);

            Assert.IsType<ConsoleDebugAdapterService>(SingleHostedService(host));
        }

        [Fact]
        public void ПеременнаяОкруженияНеЗадаётПорт()
        {
            using var env = new EnvironmentVariable("PORT", "9999");

            using var host = Program.BuildHost([]);

            var configuration = host.Services.GetRequiredService<IConfiguration>();
            Assert.Equal(4711, configuration.GetValue("port", 4711));
        }

        [Fact]
        public void АргументКоманднойСтрокиВключаетРежимОтладки()
        {
            using var host = Program.BuildHost(["--debug", "true"]);

            Assert.IsType<TcpDebugAdapterService>(SingleHostedService(host));
        }

        [Fact]
        public void АргументыКоманднойСтрокиСильнееОкружения()
        {
            using var env = new EnvironmentVariable("DEBUG", "release");

            using var host = Program.BuildHost(["--debug", "true", "--port", "4712"]);

            var configuration = host.Services.GetRequiredService<IConfiguration>();
            Assert.Equal(4712, configuration.GetValue("port", 4711));
            Assert.IsType<TcpDebugAdapterService>(SingleHostedService(host));
        }
    }

    /// <summary>Переменная окружения на время теста.</summary>
    internal sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariable(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
