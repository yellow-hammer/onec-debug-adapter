using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Кэш модулей строится по исходному коду в обоих форматах.</summary>
    public class MetadataCacheTests
    {
        private static string Fixture(params string[] segments)
            => Path.Combine([AppContext.BaseDirectory, "fixtures", .. segments]);

        private static async Task<MetadataProvider> CacheFor(string root, params (string Name, string Path)[] extensions)
        {
            var provider = new MetadataProvider(new FakeDebugConfiguration(root, extensions));
            await provider.FillMetadataCache(CancellationToken.None);
            return provider;
        }

        /// <summary>Кэш по внешним артефактам из каталога: собранные файлы лежат рядом с исходным кодом.</summary>
        private static async Task<MetadataProvider> CacheForExternal(string path)
        {
            var configuration = new FakeDebugConfiguration(
                Fixture("designer"),
                [],
                ExternalArtifacts.Descriptors(path),
                name => "C:/out/epf/" + name + ".epf");

            var provider = new MetadataProvider(configuration);
            await provider.FillMetadataCache(CancellationToken.None);
            return provider;
        }

        [Fact]
        public async Task МодулиВФорматеEDTПопадаютВКэш()
        {
            var provider = await CacheFor(Fixture("edt"));

            var commonModule = Fixture("edt", "src", "CommonModules", "ГлобальныйОбщийМодуль", "Module.bsl");
            var objectModule = Fixture("edt", "src", "Catalogs", "Справочник1", "ObjectModule.bsl");
            var formModule = Fixture("edt", "src", "Catalogs", "Справочник1", "Forms", "ФормаЭлемента", "Module.bsl");
            var commandModule = Fixture("edt", "src", "Catalogs", "Справочник1", "Commands", "Команда1", "CommandModule.bsl");

            Assert.Equal("32e087ab-1491-49b6-aba7-43571b41ac2b", provider.ModuleInfoByPath(formModule).PropertyId);
            Assert.Equal("a637f77f-3840-441d-a1c3-699c8c5cb7e0", provider.ModuleInfoByPath(objectModule).PropertyId);
            Assert.Equal("078a6af8-d22c-4248-9c33-7e90075a3d2c", provider.ModuleInfoByPath(commandModule).PropertyId);
            Assert.Equal("d5963243-262e-4398-b4d7-fb16d06484f6", provider.ModuleInfoByPath(commonModule).PropertyId);
        }

        [Fact]
        public async Task ФормаИКомандаПолучаютИдентификаторыИзMdoВладельца()
        {
            var provider = await CacheFor(Fixture("edt"));

            var formModule = Fixture("edt", "src", "Catalogs", "Справочник1", "Forms", "ФормаЭлемента", "Module.bsl");
            var commandModule = Fixture("edt", "src", "Catalogs", "Справочник1", "Commands", "Команда1", "CommandModule.bsl");

            Assert.Equal("175b035e-ee35-4fdf-a8b4-c30ce49dee61", provider.ModuleInfoByPath(formModule).ObjectId);
            Assert.Equal("342ec3c7-82d4-42bb-a5ff-8a756f110744", provider.ModuleInfoByPath(commandModule).ObjectId);
        }

        [Fact]
        public async Task ПутьМодуляВосстанавливаетсяПоИдентификаторам()
        {
            var provider = await CacheFor(Fixture("edt"));
            var objectModule = Fixture("edt", "src", "Catalogs", "Справочник1", "ObjectModule.bsl");

            var info = provider.ModuleInfoByPath(objectModule);

            Assert.Equal(objectModule, provider.ModulePathByInfo(info.Extension, info.ObjectId, info.PropertyId));
            Assert.Equal(objectModule, provider.TryModulePathByInfo(info.Extension, info.ObjectId, info.PropertyId));
        }

        [Fact]
        public async Task НеизвестныйМодульНеБросаетВTry()
        {
            var provider = await CacheFor(Fixture("edt"));

            Assert.Null(provider.TryModulePathByInfo("", "00000000-0000-0000-0000-000000000000", "11111111-1111-1111-1111-111111111111"));
            Assert.Throws<KeyNotFoundException>(() =>
                provider.ModulePathByInfo("", "00000000-0000-0000-0000-000000000000", "11111111-1111-1111-1111-111111111111"));
        }

        [Fact]
        public async Task ФорматКонфигуратораЧитаетсяПрежнимПутём()
        {
            var provider = await CacheFor(Fixture("designer"));
            var module = Fixture("designer", "CommonModules", "ГлобальныйОбщийМодуль", "Ext", "Module.bsl");

            Assert.Equal("d5963243-262e-4398-b4d7-fb16d06484f6", provider.ModuleInfoByPath(module).PropertyId);
        }

        [Fact]
        public async Task GitUriНаходитМодульКонфигуратора()
        {
            var provider = await CacheFor(Fixture("designer"));
            var module = Fixture("designer", "CommonModules", "ГлобальныйОбщийМодуль", "Ext", "Module.bsl");
            Assert.Equal(provider.ModuleInfoByPath(module), provider.ModuleInfoByPath(SourcePathTests.GitUri(module)));
        }

        [Fact]
        public async Task GitUriНаходитМодульEDT()
        {
            var provider = await CacheFor(Fixture("edt"));
            var module = Fixture("edt", "src", "Catalogs", "Справочник1", "ObjectModule.bsl");
            var form = Fixture("edt", "src", "Catalogs", "Справочник1", "Forms", "ФормаЭлемента", "Module.bsl");
            Assert.Equal(provider.ModuleInfoByPath(module), provider.ModuleInfoByPath(SourcePathTests.GitUri(module)));
            Assert.Equal(provider.ModuleInfoByPath(form), provider.ModuleInfoByPath(SourcePathTests.GitUri(form)));
        }

        [Fact]
        public async Task GitUriНаходитМодульРасширенияEDT()
        {
            var provider = await CacheFor(Fixture("designer"), ("_ДемоРасширение", Fixture("edt")));
            var module = Fixture("edt", "src", "CommonModules", "ГлобальныйОбщийМодуль", "Module.bsl");
            Assert.Equal("_ДемоРасширение", provider.ModuleInfoByPath(SourcePathTests.GitUri(module)).Extension);
        }

        [Fact]
        public async Task РасширениеEDTЧитаетсяКакОтдельныйКорень()
        {
            var provider = await CacheFor(Fixture("designer"), ("_ДемоРасширение", Fixture("edt")));
            var module = Fixture("edt", "src", "CommonModules", "ГлобальныйОбщийМодуль", "Module.bsl");

            Assert.Equal("_ДемоРасширение", provider.ModuleInfoByPath(module).Extension);
        }

        [Fact]
        public async Task МодулиВнешнихАртефактовВФорматеEDTПопадаютВКэш()
        {
            var provider = await CacheForExternal(Fixture("external", "edt"));

            var objectModule = Fixture("external", "edt", "src", "ExternalDataProcessors", "ТестоваяВнешняяОбработка", "ObjectModule.bsl");
            var formModule = Fixture("external", "edt", "src", "ExternalReports", "ТестовыйВнешнийОтчет", "Forms", "ФормаВарианта", "Module.bsl");

            Assert.Equal("4c1090aa-a76e-4693-87c0-6f4b7494467d", provider.ModuleInfoByPath(objectModule).ObjectId);
            Assert.Equal("a637f77f-3840-441d-a1c3-699c8c5cb7e0", provider.ModuleInfoByPath(objectModule).PropertyId);
            Assert.Equal("a1c674e3-79da-4ea4-8776-9860a0d1f750", provider.ModuleInfoByPath(formModule).ObjectId);
            Assert.Equal("32e087ab-1491-49b6-aba7-43571b41ac2b", provider.ModuleInfoByPath(formModule).PropertyId);
        }

        [Fact]
        public async Task МодулиВнешнихАртефактовАдресуютсяПоСобранномуФайлу()
        {
            var provider = await CacheForExternal(Fixture("external", "edt"));
            var objectModule = Fixture("external", "edt", "src", "ExternalDataProcessors", "ТестоваяВнешняяОбработка", "ObjectModule.bsl");

            var info = provider.ModuleInfoByPath(objectModule);

            Assert.True(provider.IsExternalModule(info));
            Assert.Equal("file://C:/out/epf/ТестоваяВнешняяОбработка.epf", provider.ExternalModuleUrl(info));
        }

        [Fact]
        public async Task ОдинАртефактВРазныхФорматахДаётОдниИдентификаторы()
        {
            var edt = await CacheForExternal(Fixture("external", "edt"));
            var designer = await CacheForExternal(Fixture("external", "designer"));

            var edtFormModule = Fixture("external", "edt", "src", "ExternalDataProcessors", "ТестоваяВнешняяОбработка", "Forms", "Форма", "Module.bsl");
            var designerFormModule = Fixture("external", "designer", "ТестоваяВнешняяОбработка", "Forms", "Форма", "Ext", "Form", "Module.bsl");

            Assert.Equal(designer.ModuleInfoByPath(designerFormModule), edt.ModuleInfoByPath(edtFormModule));
        }
    }

    /// <summary>Конфигурация отладки с заданными корнями исходников.</summary>
    internal sealed class FakeDebugConfiguration(
        string rootProject,
        (string Name, string Path)[] extensions,
        IReadOnlyList<string>? externalSources = null,
        Func<string, string?>? externalBuildFile = null) : IDebugConfiguration
    {
        public Task Initialization => Task.CompletedTask;
        public InfoBaseItem InfoBase => new("test", new Dictionary<string, string?>());
        public bool IsFileInfoBase => true;
        public string InfoBaseName => "test";
        public string PlatformBin => string.Empty;
        public string DebuggerID => "test";
        public string DebugServerHost => "localhost";
        public int DebugServerPort => 1550;
        public string RootProject => rootProject;
        public IReadOnlyDictionary<string, string> Extensions => extensions.ToDictionary(item => item.Name, item => item.Path);
        public IReadOnlyList<string> ExternalSources => externalSources ?? [];
        public string? ExternalBuildFile(string artifactName) => externalBuildFile?.Invoke(artifactName);
        public DebugTargetType[] InitialTargetTypes => [];
        public int PollMinDelayMs => 25;
        public int PollMaxDelayMs => 200;
        public int CalcWaitingTimeMs => 100;
        public IReadOnlyList<int> VariablesRetryDelaysMs => [];
        public bool DiagnosticLogging => false;
        public string User => string.Empty;
        public string Password => string.Empty;
        public void SetDebugServerPort(int port) { }
        public T CreateRequest<T>() where T : RDbgBaseRequest, new() => new();
        public T CreateRequest<T>(Action<T> factory) where T : RDbgBaseRequest, new() => new();
        public Task Init(Dictionary<string, JToken> arguments) => Task.CompletedTask;
    }
}
