using System.Diagnostics;
using System.Text;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// Запуск клиента 1С вне дерева процессов адаптера.
    /// </summary>
    /// <remarks>
    /// На Windows VS Code останавливает адаптер через <c>taskkill /T /F</c> через две секунды после
    /// <c>disconnect</c>. Прямой дочерний процесс адаптера при этом тоже убивается, а убитый клиент
    /// оставляет в кластере спящий сеанс. Поэтому клиент запускает короткоживущий промежуточный
    /// процесс адаптера: он стартует клиент и сразу выходит, возвращая его pid кодом возврата.
    /// После выхода промежуточного процесса клиент больше не входит в дерево адаптера.
    /// </remarks>
    internal static class DetachedLaunch
    {
        internal const string Argument = "--launch-detached";

        private static readonly TimeSpan LauncherTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Режим промежуточного процесса: запускает клиент и возвращает его pid.</summary>
        internal static int RunLauncher(string encodedFile, string encodedArguments)
        {
            try
            {
                // ShellExecute не передаёт клиенту дескрипторы промежуточного процесса.
                using var process = Process.Start(new ProcessStartInfo(Decode(encodedFile), Decode(encodedArguments))
                {
                    UseShellExecute = true
                });
                return process?.Id ?? -1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[onec-debug-adapter] клиент 1С не запущен: {ex.Message}");
                return -1;
            }
        }

        internal static Process Start(string file, string arguments)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Вне Windows VS Code завершает только процесс адаптера, дочерние процессы живут дальше.
                return Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })
                    ?? throw new InvalidOperationException("Клиент 1С не запустился");
            }

            var launcher = new ProcessStartInfo(LauncherHost())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Свои потоки, а не канал DAP: иначе клиент унаследовал бы канал отладчика.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in LauncherPrefix())
                launcher.ArgumentList.Add(argument);
            launcher.ArgumentList.Add(Argument);
            launcher.ArgumentList.Add(Encode(file));
            launcher.ArgumentList.Add(Encode(arguments));

            using var process = Process.Start(launcher) ?? throw new InvalidOperationException("Клиент 1С не запустился");
            if (!process.WaitForExit(LauncherTimeout))
            {
                process.Kill();
                throw new InvalidOperationException("Клиент 1С не запустился за отведённое время");
            }

            var pid = process.ExitCode;
            if (pid <= 0)
                throw new InvalidOperationException($"Клиент 1С не запустился: {process.StandardError.ReadToEnd().Trim()}");

            return Process.GetProcessById(pid);
        }

        internal static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        internal static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

        /// <summary>Исполняемый файл адаптера: нативный хост или dotnet для переносимой сборки.</summary>
        private static string LauncherHost()
            => Environment.ProcessPath ?? throw new InvalidOperationException("Не определён путь к адаптеру");

        /// <summary>Для <c>dotnet OnecDebugAdapter.dll</c> перед режимом нужен путь к сборке.</summary>
        private static IEnumerable<string> LauncherPrefix()
        {
            var host = Path.GetFileNameWithoutExtension(LauncherHost());
            if (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                yield return typeof(DetachedLaunch).Assembly.Location;
        }
    }
}
