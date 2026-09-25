namespace Onec.DebugAdapter.DebugServer
{
    /// <summary>
    /// По ошибкам опроса решает, продолжается ли отладка на сервере отладки.
    /// Отказ 400 значит, что сервер отладчика не знает: его перезапустили, и регистрация не вернётся.
    /// Сервер, который не отвечает дольше <paramref name="unavailableTimeout"/>, считается остановленным.
    /// </summary>
    internal sealed class DebugServerLiveness(TimeSpan unavailableTimeout)
    {
        private DateTime? _failingSince;

        public void Succeeded() => _failingSince = null;

        /// <returns>Причина завершения отладки или null, если опрос стоит повторить.</returns>
        public string? Failed(System.Exception error, DateTime now)
        {
            if (error is DebugServerException { StatusCode: 400 })
                return "Сервер отладки перезапущен, отладка завершена.";

            _failingSince ??= now;
            return now - _failingSince >= unavailableTimeout
                ? "Сервер отладки не отвечает, отладка завершена."
                : null;
        }
    }
}
