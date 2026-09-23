using Onec.DebugAdapter.DebugServer;

namespace Onec.DebugAdapter.Services
{
    /// <summary>
    /// Предметы отладки сеанса, который открыл запущенный адаптером клиент.
    /// </summary>
    /// <remarks>
    /// При отладке клиент-серверной базы серверные предметы есть у сеансов всех пользователей,
    /// и автоподключение подключает их все. Завершать все подключённые предметы нельзя: на общем
    /// сервере это обрывает чужие сеансы. Свой сеанс — тот, где после запуска клиента появился
    /// новый предмет клиента. Пока клиент загружается и своего предмета ещё нет, сеанс узнаём
    /// по новому серверному предмету.
    /// </remarks>
    internal static class ClientSession
    {
        internal static IReadOnlyList<DebugTargetId> Targets(IReadOnlySet<string> beforeLaunch, IEnumerable<DebugTargetId> current)
        {
            var targets = current
                .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.SeanceId))
                .DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fresh = targets.Where(t => !beforeLaunch.Contains(t.Id)).ToList();

            var seances = Seances(fresh.Where(t => IsClient(t.TargetType)));
            if (seances.Count == 0)
                seances = Seances(fresh.Where(t => IsServer(t.TargetType)));

            return targets.Where(t => seances.Contains(t.SeanceId)).ToList();
        }

        private static HashSet<string> Seances(IEnumerable<DebugTargetId> targets)
            => targets.Select(t => t.SeanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool IsClient(DebugTargetType type)
            => type is DebugTargetType.ManagedClient or DebugTargetType.Client;

        private static bool IsServer(DebugTargetType type)
            => type is DebugTargetType.Server or DebugTargetType.ServerEmulation;
    }
}
