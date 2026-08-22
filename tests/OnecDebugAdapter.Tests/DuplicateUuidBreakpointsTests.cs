using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Точки останова во внешних обработках, у которых совпадает uuid.
    ///
    /// Карты точек адресовались тройкой идентификаторов, общей у копий: вторая
    /// копия вытесняла первую, и сервер получал точки только одной из них.
    /// </summary>
    public class DuplicateUuidBreakpointsTests
    {
        private const string ObjectId = "b41d8e07-5c26-4a93-9e08-7f6d3b12ac54";
        private const string PropertyId = "32e087ab-1491-49b6-aba7-43571b41ac2b";

        private static readonly string FirstPath = Path.Combine(Path.GetTempPath(), "ТестДубльПервая", "ObjectModule.bsl");
        private static readonly string SecondPath = Path.Combine(Path.GetTempPath(), "ТестДубльВторая", "ObjectModule.bsl");
        private const string FirstUrl = "file://C:/out/epf/ТестДубльПервая.epf";
        private const string SecondUrl = "file://C:/out/epf/ТестДубльВторая.epf";

        [Fact]
        public async Task ТочкиОбеихКопийУходятВЗапрос()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4, 13));
            await manager.SetBreakpoints(Args(SecondPath, 7));

            var urls = client.LastRequest!.BpWorkspace.Select(m => m.Id.Url).ToList();
            Assert.Equal(2, urls.Count);
            Assert.Contains(FirstUrl, urls);
            Assert.Contains(SecondUrl, urls);
        }

        [Fact]
        public async Task ТочкиКопииНеПропадаютПослеСоседней()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4, 13));
            await manager.SetBreakpoints(Args(SecondPath, 7));

            var first = client.LastRequest!.BpWorkspace.Single(m => m.Id.Url == FirstUrl);
            Assert.Equal([4, 13], first.BpInfo.Select(b => (int)b.Line));
        }

        [Fact]
        public async Task ОтветОтдаётТочкиЗапрошенногоФайла()
        {
            var (manager, _) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4, 13));
            var response = await manager.SetBreakpoints(Args(SecondPath, 7));

            Assert.Equal([7], response.Breakpoints.Select(b => b.Line));
        }

        /// <summary>Идентификаторы точек не должны пересекаться: по ним адресуется коррекция строк.</summary>
        [Fact]
        public async Task ИдентификаторыТочекУКопийРазные()
        {
            var (manager, _) = Manager();

            var first = await manager.SetBreakpoints(Args(FirstPath, 4));
            var second = await manager.SetBreakpoints(Args(SecondPath, 4));

            Assert.NotEqual(first.Breakpoints[0].Id, second.Breakpoints[0].Id);
        }

        /// <summary>Повторный запрос по тому же файлу заменяет его точки, а не добавляет модуль.</summary>
        [Fact]
        public async Task ПовторныйЗапросНеЗадваиваетМодуль()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4));
            await manager.SetBreakpoints(Args(FirstPath, 4, 13));

            var module = Assert.Single(client.LastRequest!.BpWorkspace);
            Assert.Equal([4, 13], module.BpInfo.Select(b => (int)b.Line));
        }

        /// <summary>Файл из вкладки Git приходит как git-URI: это тот же модуль, а не второй.</summary>
        [Fact]
        public async Task ТочкиИзВкладкиGitАдресуютТотЖеМодуль()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4));
            await manager.SetBreakpoints(Args(SourcePathTests.GitUri(FirstPath), 4, 13));

            var module = Assert.Single(client.LastRequest!.BpWorkspace);
            Assert.Equal([4, 13], module.BpInfo.Select(b => (int)b.Line));
        }

        /// <summary>
        /// Регистр пути ключ не меняет: так же устроен кэш путей модулей, иначе один и тот же
        /// модуль попал бы в запрос дважды.
        /// </summary>
        [Fact]
        public async Task РегистрПутиНеДелаетВторойМодуль()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(FirstPath, 4));
            await manager.SetBreakpoints(Args(FirstPath.ToUpperInvariant(), 4, 13));

            var module = Assert.Single(client.LastRequest!.BpWorkspace);
            Assert.Equal([4, 13], module.BpInfo.Select(b => (int)b.Line));
        }

        private static (StoppingManager Manager, FakeDebugServerClient Client) Manager()
        {
            var client = new FakeDebugServerClient();
            var manager = new StoppingManager(new FakeDebugConfiguration(string.Empty, []), new StubMetadata(), client, new FakeDebugServerListener(), new FakeDebugTargetsManager());
            return (manager, client);
        }

        private static SetBreakpointsArguments Args(string path, params int[] lines)
            => new()
            {
                Source = new Source { Path = path },
                Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToList()
            };

        private sealed class StubMetadata : IMetadataProvider
        {
            public Task Init(DebugProtocolClient client, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => FirstPath;
            public string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => FirstPath;
            public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default)
                => ("", ObjectId, PropertyId);
            public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info) => true;
            public string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info) => FirstUrl;
            public string ExternalModuleUrlByPath(string path)
                => SourcePath.Resolve(path).Contains("Вторая", StringComparison.Ordinal) ? SecondUrl : FirstUrl;
            public string? TryModulePathByExternalUrl(string url, string propertyId)
                => url == SecondUrl ? SecondPath : FirstPath;
            public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info) => FirstPath;
            public IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info)
                => [];
        }
    }
}
