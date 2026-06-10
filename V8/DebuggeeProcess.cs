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

        public void Run(DebugProtocolClient client)
        {
            _client = client;

            var connectionString = _configuration.InfoBase.Connect ?? "";
            var arguments = new List<string>
            {
                connectionString.Contains(' ') ? $"\"{connectionString}\"" : connectionString,
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
                Log.Debug($"автовход: пользователь=\"{_configuration.User}\", пароль={(string.IsNullOrEmpty(_configuration.Password) ? "(пусто)" : "***")}");
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
