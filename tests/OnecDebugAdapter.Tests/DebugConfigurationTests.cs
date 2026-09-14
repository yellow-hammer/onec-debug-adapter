using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.Services;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Свой сервер отладки адаптер поднимает только при запуске файловой базы.
    /// При присоединении сервер уже работает: автономный сервер с --debug=http держит его сам.
    /// </summary>
    public class DebugConfigurationTests
    {
        private static string Fixture(params string[] segments)
            => Path.Combine([AppContext.BaseDirectory, "fixtures", .. segments]);

        private static Dictionary<string, JToken> Arguments(string request, string connectionString) => new()
        {
            ["request"] = request,
            ["connectionString"] = connectionString,
            ["platformPath"] = Fixture("platform"),
            ["debugServerHost"] = "localhost",
            ["debugServerPort"] = 1650,
            ["rootProject"] = string.Empty,
            ["user"] = string.Empty,
            ["password"] = string.Empty,
        };

        [Fact]
        public async Task ПрисоединениеКФайловойБазеИдётКСерверуПоПортуИзНастроек()
        {
            var configuration = new DebugConfiguration();

            await configuration.Init(Arguments("attach", @"/FC:\proj\build\ib"));

            Assert.True(configuration.IsFileInfoBase);
            Assert.False(configuration.OwnsDebugServer);
            Assert.Equal(1650, configuration.DebugServerPort);
            Assert.True(configuration.Initialization.IsCompleted);
        }

        [Fact]
        public async Task ПрисоединениеКСервернойБазеИдётКСерверуПоПортуИзНастроек()
        {
            var configuration = new DebugConfiguration();

            await configuration.Init(Arguments("attach", @"/Ssrv\base"));

            Assert.False(configuration.OwnsDebugServer);
            Assert.Equal(1650, configuration.DebugServerPort);
            Assert.True(configuration.Initialization.IsCompleted);
        }

        [Fact]
        public async Task ЗапускФайловойБазыЖдётПортСвоегоСервераОтладки()
        {
            var configuration = new DebugConfiguration();

            await configuration.Init(Arguments("launch", @"/FC:\proj\build\ib"));

            Assert.True(configuration.OwnsDebugServer);
            Assert.False(configuration.Initialization.IsCompleted);

            configuration.SetDebugServerPort(1553);

            Assert.Equal(1553, configuration.DebugServerPort);
            Assert.True(configuration.Initialization.IsCompleted);
        }
    }
}
