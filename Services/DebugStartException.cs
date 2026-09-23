namespace Onec.DebugAdapter.Services
{
    /// <summary>
    /// Отказ запуска отладки с текстом для пользователя: VS Code показывает его вместо общего
    /// «Ошибка запуска отладки», технические подробности остаются в журнале адаптера.
    /// </summary>
    internal sealed class DebugStartException(string message) : System.Exception(message)
    {
        /// <summary>
        /// Сервер отладки не отвечает. Для клиент-серверной базы его поднимает служба агента,
        /// только если она запущена с ключами -debug -http: без -http отладка идёт по TCP и
        /// HTTP-сервера отладки нет.
        /// </summary>
        internal static DebugStartException DebugServerUnavailable(string host, int port, bool clusterService)
            => new(clusterService
                ? $"Сервер отладки {host}:{port} недоступен. Запустите службу сервера 1С с ключами -debug -http или укажите debugServerHost и debugServerPort."
                : $"Сервер отладки {host}:{port} недоступен. Проверьте debugServerHost и debugServerPort.");
    }
}
