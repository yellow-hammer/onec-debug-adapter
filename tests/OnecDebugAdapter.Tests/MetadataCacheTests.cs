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
        public async Task КопииОбработокСОдинаковымUuidНеДелятСобранныйФайл()
        {
            var provider = await CacheForExternal(Fixture("duplicate-uuid"));
            var first = Fixture("duplicate-uuid", "ПерваяКопия", "Ext", "ObjectModule.bsl");
            var second = Fixture("duplicate-uuid", "ВтораяКопия", "Ext", "ObjectModule.bsl");

            // Копия обработки сохраняет uuid оригинала, поэтому тройка идентификаторов
            // у них общая: так устроена и демонстрационная конфигурация SSL.
            Assert.Equal(provider.ModuleInfoByPath(first), provider.ModuleInfoByPath(second));

            Assert.Equal("file://C:/out/epf/ПерваяКопия.epf", provider.ExternalModuleUrlByPath(first));
            Assert.Equal("file://C:/out/epf/ВтораяКопия.epf", provider.ExternalModuleUrlByPath(second));
        }

        [Fact]
        public async Task КопииОтчётовСОдинаковымUuidТожеРазличаются()
        {
            var provider = await CacheForExternal(Fixture("duplicate-uuid"));
            var first = Fixture("duplicate-uuid", "ПервыйОтчёт", "Ext", "ObjectModule.bsl");
            var second = Fixture("duplicate-uuid", "ВторойОтчёт", "Ext", "ObjectModule.bsl");

            Assert.Equal(provider.ModuleInfoByPath(first), provider.ModuleInfoByPath(second));

            Assert.Equal("file://C:/out/epf/ПервыйОтчёт.epf", provider.ExternalModuleUrlByPath(first));
            Assert.Equal("file://C:/out/epf/ВторойОтчёт.epf", provider.ExternalModuleUrlByPath(second));
        }

        [Theory]
        [InlineData("ExternalDataProcessors", "ПерваяКопия", "ВтораяКопия")]
        [InlineData("ExternalReports", "ПервыйОтчёт", "ВторойОтчёт")]
        public async Task КопииВФорматеEDTТожеРазличаются(string mdType, string firstName, string secondName)
        {
            var provider = await CacheForExternal(Fixture("duplicate-uuid-edt"));
            var first = Fixture("duplicate-uuid-edt", "src", mdType, firstName, "ObjectModule.bsl");
            var second = Fixture("duplicate-uuid-edt", "src", mdType, secondName, "ObjectModule.bsl");

            Assert.Equal(provider.ModuleInfoByPath(first), provider.ModuleInfoByPath(second));

            Assert.Equal($"file://C:/out/epf/{firstName}.epf", provider.ExternalModuleUrlByPath(first));
            Assert.Equal($"file://C:/out/epf/{secondName}.epf", provider.ExternalModuleUrlByPath(second));
        }

        [Fact]
        public async Task UrlСобранногоФайлаНаходитсяИПоGitUri()
        {
            // Путь из редактора приходит и во вкладке Git: поиск URL обязан
            // проходить тот же резолв, что и поиск модуля.
            var provider = await CacheForExternal(Fixture("duplicate-uuid"));
            var second = Fixture("duplicate-uuid", "ВтораяКопия", "Ext", "ObjectModule.bsl");

            Assert.Equal(
                "file://C:/out/epf/ВтораяКопия.epf",
                provider.ExternalModuleUrlByPath(SourcePathTests.GitUri(second)));
        }

        [Fact]
        public async Task ПутьИсходникаНаходитсяПоUrlСобранногоФайла()
        {
            var provider = await CacheForExternal(Fixture("duplicate-uuid"));
            var second = Fixture("duplicate-uuid", "ВтораяКопия", "Ext", "ObjectModule.bsl");
            var propertyId = provider.ModuleInfoByPath(second).PropertyId;

            Assert.Equal(
                second,
                provider.TryModulePathByExternalUrl("file://C:/out/epf/ВтораяКопия.epf", propertyId));
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
}
