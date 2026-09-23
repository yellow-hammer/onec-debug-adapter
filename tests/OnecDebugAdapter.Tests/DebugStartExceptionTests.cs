using Onec.DebugAdapter.Services;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Недоступный сервер отладки: вместо общего «Ошибка запуска отладки» пользователь видит,
    /// куда адаптер стучался и что проверить.
    /// </summary>
    public class DebugStartExceptionTests
    {
        [Fact]
        public void Для_серверной_базы_подсказаны_ключи_службы()
        {
            var message = DebugStartException.DebugServerUnavailable("srv-1c", 2550, clusterService: true).Message;

            Assert.Contains("srv-1c:2550", message);
            Assert.Contains("-debug -http", message);
            Assert.Contains("debugServerPort", message);
        }

        [Fact]
        public void При_присоединении_ключи_службы_не_упоминаются()
        {
            var message = DebugStartException.DebugServerUnavailable("localhost", 1550, clusterService: false).Message;

            Assert.Contains("localhost:1550", message);
            Assert.DoesNotContain("-debug", message);
        }
    }
}
