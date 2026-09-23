using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// При остановке отладки завершается только сеанс запущенного клиента: серверные предметы
    /// есть у сеансов всех пользователей базы, и чужие сеансы обрывать нельзя.
    /// </summary>
    public class ClientSessionTests
    {
        private const string Own = "11111111-1111-1111-1111-111111111111";
        private const string Colleague = "22222222-2222-2222-2222-222222222222";
        private const string Job = "33333333-3333-3333-3333-333333333333";

        [Fact]
        public void ЗавершаетсяСеансНовогоКлиентаСоВсемиПредметами()
        {
            var colleagueServer = Target("a", Colleague, DebugTargetType.Server);
            var ownServer = Target("b", Own, DebugTargetType.Server);
            var ownClient = Target("c", Own, DebugTargetType.ManagedClient);

            var targets = ClientSession.Targets(Before("a"), [colleagueServer, ownServer, ownClient]);

            Assert.Equal(["b", "c"], targets.Select(t => t.Id).Order());
        }

        [Fact]
        public void ЧужойСеансПоявившийсяПослеЗапускаНеТрогается()
        {
            var ownClient = Target("c", Own, DebugTargetType.ManagedClient);
            var colleagueServer = Target("d", Colleague, DebugTargetType.Server);

            var targets = ClientSession.Targets(Before(), [ownClient, colleagueServer]);

            Assert.Equal(["c"], targets.Select(t => t.Id));
        }

        [Fact]
        public void ПокаКлиентЗагружаетсяСеансУзнаётсяПоСерверномуПредмету()
        {
            var colleagueServer = Target("a", Colleague, DebugTargetType.Server);
            var ownServer = Target("b", Own, DebugTargetType.Server);

            var targets = ClientSession.Targets(Before("a"), [colleagueServer, ownServer]);

            Assert.Equal(["b"], targets.Select(t => t.Id));
        }

        [Fact]
        public void ФоновоеЗаданиеНеСчитаетсяСеансомКлиента()
        {
            var job = Target("j", Job, DebugTargetType.Job);

            Assert.Empty(ClientSession.Targets(Before(), [job]));
        }

        [Fact]
        public void БезНовыхПредметовЗавершатьНечего()
        {
            var colleagueServer = Target("a", Colleague, DebugTargetType.Server);

            Assert.Empty(ClientSession.Targets(Before("a"), [colleagueServer]));
        }

        [Fact]
        public void ПодключённыйИДоступныйПредметНеДублируется()
        {
            var ownClient = Target("c", Own, DebugTargetType.ManagedClient);
            var sameClient = Target("C", Own, DebugTargetType.ManagedClient);

            Assert.Single(ClientSession.Targets(Before(), [ownClient, sameClient]));
        }

        private static HashSet<string> Before(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

        private static DebugTargetId Target(string id, string seance, DebugTargetType type)
            => new() { Id = id, SeanceId = seance, TargetType = type };
    }
}
