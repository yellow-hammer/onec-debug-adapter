using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Onec.DebugAdapter.V8
{
    public class DebuggeeProcess : IDisposable
    {
        private readonly IDebugConfiguration _configuration;
        private DebugProtocolClient _client = null!;
        private bool _needSendEvent = true;

        private Process? _process;
        private bool disposedValue;

        public DebuggeeProcess(IDebugConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Заключает в кавычки значение строки подключения, а не весь ключ.
        /// </summary>
        /// <remarks>
        /// Клиент 1С разбирает командную строку сам и ждёт форму <c>/F"путь"</c>.
        /// Кавычки вокруг всего токена (<c>"/Fпуть"</c>) он не понимает: путь с
        /// пробелом обрезается по первому пробелу, и база не открывается.
        /// Конфигуратор такую форму принимает, тонкий клиент - нет.
        /// </remarks>
        /// <param name="connect">Строка подключения из конфигурации запуска.</param>
        /// <returns>Аргумент командной строки клиента.</returns>
        internal static string QuoteConnectString(string connect)
        {
            var trimmed = (connect ?? "").Trim();
            if (trimmed.Length < 2)
                return trimmed;

            var key = trimmed[..2];
            if (!key.Equals("/F", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("/S", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            var value = trimmed[2..].Trim().Trim('"');
            return value.Length == 0 ? trimmed : $"{key}\"{value}\"";
        }

        /// <summary>Пароль автовхода в журнал не попадает.</summary>
        internal static string HidePassword(string argument)
            => argument.StartsWith("/P\"", StringComparison.Ordinal) && argument.Length > 4
                ? "/P\"***\""
                : argument;

        public void Run(DebugProtocolClient client)
        {
            _client = client;

            var connectionString = _configuration.InfoBase.Connect ?? "";
            var arguments = new List<string>
            {
                QuoteConnectString(connectionString),
                "/TCOMP -SDC",
                "/DisableStartupMessages",
                "/DisplayPerformance",
                "/TechnicalSpecialistMode",
                "/DEBUG -http -attach",
                $"/DEBUGGERURL \"http://{_configuration.DebugServerHost}:{_configuration.DebugServerPort}\"",
                "/O Normal"
            };

            // Автовход: с учётными данными клиент стартует без окна аутентификации.
            if (!string.IsNullOrEmpty(_configuration.User) || !string.IsNullOrEmpty(_configuration.Password))
            {
                arguments.Add("/WA-");
                arguments.Add($"/N\"{_configuration.User}\"");
                arguments.Add($"/P\"{_configuration.Password}\"");
            }

            var exePath = Path.Join(
                _configuration.PlatformBin, 
                Environment.OSVersion.Platform switch
                {
                    PlatformID.Win32NT => "1cv8c.exe",
                    _ => "1cv8c"
                });
            if (!File.Exists(exePath))
                throw new Exception("Исполняемый файл клиента 1С не найден");

            Log.Debug($"клиент 1С: {exePath} {string.Join(" ", arguments.Select(HidePassword))}");

			_process = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, string.Join(" ", arguments))
				{
					RedirectStandardError = true
				},
                EnableRaisingEvents = true
            };
			_process.Exited += DebuggeeExited;
			_process.Start();
        }

        private void DebuggeeExited(object? sender, EventArgs e)
        {
            if (_needSendEvent)
            {
				if (_process?.ExitCode != 0)
					_client.SendError(_process?.StandardError.ReadToEnd() ?? "");

				_client?.SendEvent(new TerminatedEvent());
			}
        }

        public void Stop()
        {
            _needSendEvent = false;
            _process?.Kill();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: освободить управляемое состояние (управляемые объекты)
                }

                Stop();
                disposedValue = true;
            }
        }

        // TODO: переопределить метод завершения, только если "Dispose(bool disposing)" содержит код для освобождения неуправляемых ресурсов
        ~DebuggeeProcess()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
