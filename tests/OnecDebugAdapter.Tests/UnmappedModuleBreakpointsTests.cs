using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Точка в файле вне структуры конфигурации: соседний проект в рабочей области,
    /// расширение или обработка, которых нет среди каталогов исходного кода.
    /// </summary>
    public class UnmappedModuleBreakpointsTests
    {
        private const string ObjectId = "7c1e4a2b-3d5f-4e6a-8b9c-0d1e2f3a4b5c";
        private const string PropertyId = "d5963243-262e-4398-b4d7-fb16d06484f6";

        private static readonly string KnownPath = Path.Combine(Path.GetTempPath(), "ТестКонфигурация", "Module.bsl");
        private static readonly string ForeignPath = Path.Combine(Path.GetTempPath(), "ТестЧужойПроект", "Module.bsl");

        [Fact]
        public async Task ТочкаВнеСтруктурыНеСбрасываетОстальные()
        {
            var (manager, client) = Manager();

            await manager.SetBreakpoints(Args(ForeignPath, 3));
            var response = await manager.SetBreakpoints(Args(KnownPath, 5));

            var module = Assert.Single(client.LastRequest!.BpWorkspace);
            Assert.Equal(ObjectId, module.Id.ObjectId);
            Assert.True(Assert.Single(response.Breakpoints).Verified);
        }

        [Fact]
        public async Task ТочкаВнеСтруктурыНеПодтвержденаИОбъясняетПричину()
        {
            var (manager, _) = Manager();

            var response = await manager.SetBreakpoints(Args(ForeignPath, 3));

            var breakpoint = Assert.Single(response.Breakpoints);
            Assert.False(breakpoint.Verified);
            Assert.Contains("не найден в структуре конфигурации", breakpoint.Message);
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
                => KnownPath;
            public string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => KnownPath;
            public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default)
                => SourcePath.Resolve(path).Contains("ЧужойПроект", StringComparison.Ordinal)
                    ? throw new KeyNotFoundException($"Путь к модулю не найден в структуре конфигурации: {path}.")
                    : ("", ObjectId, PropertyId);
            public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info) => false;
            public string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info) => "";
            public string ExternalModuleUrlByPath(string path) => "";
            public string? TryModulePathByExternalUrl(string url, string propertyId) => null;
            public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info) => null;
            public IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info)
                => [];
        }
    }
}
