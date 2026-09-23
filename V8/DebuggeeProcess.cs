using Onec.DebugAdapter.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// Клиент 1С, запущенный адаптером.
    /// </summary>
    /// <remarks>
    /// Адаптер клиент не убивает: убитый клиент оставляет в кластере спящий сеанс, и тот висит
    /// до суток. Клиент закрывается сам — по команде сервера отладки или обычным закрытием окна.
    /// </remarks>
    public class DebuggeeProcess : IDisposable
    {
        private readonly IDebugConfiguration _configuration;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Process? _process;
        private bool disposedValue;

        /// <summary>Клиент завершился; аргумент — код возврата, если он известен.</summary>
        public event Action<int?>? Exited;

        public bool HasExited => _exited.Task.IsCompleted;

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

        public void Run()
        {
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

            Process process;
            try
            {
                process = DetachedLaunch.Start(exePath, string.Join(" ", arguments));
            }
            catch (ArgumentException)
            {
                // Клиент успел выйти раньше, чем адаптер его нашёл.
                MarkExited(null);
                return;
            }

            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => MarkExited(ExitCode(process));
            if (process.HasExited)
                MarkExited(ExitCode(process));
        }

        /// <summary>Ждёт выхода клиента. true, если клиент вышел за отведённое время.</summary>
        public async Task<bool> WaitForExit(TimeSpan timeout)
        {
            if (_process == null)
                return true;

            return await Task.WhenAny(_exited.Task, Task.Delay(timeout)) == _exited.Task;
        }

        /// <summary>Обычное закрытие главного окна: клиент выходит сам и заканчивает свой сеанс.</summary>
        public bool RequestClose()
        {
            var process = _process;
            if (process == null || HasExited)
                return false;

            try
            {
                process.Refresh();
                return process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void MarkExited(int? exitCode)
        {
            if (_exited.TrySetResult())
                Exited?.Invoke(exitCode);
        }

        private static int? ExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                _process?.Dispose();
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
