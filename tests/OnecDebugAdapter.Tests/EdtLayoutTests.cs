using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Разбор исходного кода в формате EDT.</summary>
    public class EdtLayoutTests
    {
        private static string Edt(params string[] segments)
            => Path.Combine([AppContext.BaseDirectory, "fixtures", "edt", .. segments]);

        private static string CatalogMdo()
            => Edt("src", "Catalogs", "Справочник1", "Справочник1.mdo");

        [Fact]
        public void КаталогИсходногоКодаНаходитсяПоПроектуEDT()
        {
            Assert.Equal(Edt("src"), EdtLayout.FindSourcesRoot(Edt()));
        }

        [Fact]
        public void КаталогИсходногоКодаНаходитсяИПоСамомуКаталогуSrc()
        {
            Assert.Equal(Edt("src"), EdtLayout.FindSourcesRoot(Edt("src")));
        }

        [Fact]
        public void ПроектEDTЧитаетсяКакКаталогИсходногоКода()
        {
            // Каталог из настройки paths.cf расширения (по умолчанию src/cf).
            var projectSources = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "src", "cf");
            Directory.CreateDirectory(projectSources);
            CopyDirectory(Edt(), projectSources);

            Assert.Equal(Path.Combine(projectSources, "src"), EdtLayout.FindSourcesRoot(projectSources));
        }

        private static void CopyDirectory(string source, string target)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(source, target));

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target), true);
        }

        [Fact]
        public void ФорматКонфигуратораЗаEDTНеПринимается()
        {
            var designerDump = Path.Combine(AppContext.BaseDirectory, "fixtures", "designer");

            Assert.Null(EdtLayout.FindSourcesRoot(designerDump));
        }

        [Fact]
        public void ИдентификаторКонфигурацииБерётсяИзMdo()
        {
            var mdo = Edt("src", "Configuration", "Configuration.mdo");

            Assert.Equal("46c7c1d0-b04d-4295-9b04-ae3207c18d29", EdtLayout.ObjectId(mdo));
        }

        [Fact]
        public void ИдентификаторОбъектаБерётсяИзMdo()
        {
            Assert.Equal("eeef463d-d5e7-42f2-ae53-10279661f59d", EdtLayout.ObjectId(CatalogMdo()));
        }

        [Fact]
        public void ИдентификаторыФормБерутсяИзMdoВладельца()
        {
            var forms = EdtLayout.ReadObject(CatalogMdo()).Forms;

            Assert.Equal("175b035e-ee35-4fdf-a8b4-c30ce49dee61", forms["ФормаЭлемента"]);
            Assert.Equal("1feb8a5b-989e-440d-afe3-9472183c335c", forms["ФормаСписка"]);
        }

        [Fact]
        public void ИдентификаторыКомандБерутсяИзMdoВладельца()
        {
            Assert.Equal("342ec3c7-82d4-42bb-a5ff-8a756f110744", EdtLayout.ReadObject(CatalogMdo()).Commands["Команда1"]);
        }

        [Fact]
        public void МодулиОбъектаЛежатРядомСMdo()
        {
            var modules = EdtLayout.ModulesIn(Edt("src", "Catalogs", "Справочник1"))
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();

            Assert.Equal(["ManagerModule.bsl", "ObjectModule.bsl"], modules);
        }

        [Fact]
        public void МодулиКонфигурацииЛежатРядомСConfigurationMdo()
        {
            var modules = EdtLayout.ModulesIn(Edt("src", "Configuration"))
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();

            Assert.Contains("ManagedApplicationModule.bsl", modules);
            Assert.Contains("SessionModule.bsl", modules);
        }


        [Fact]
        public void ИмяРасширенияБерётсяИзMdoПроекта()
        {
            var mdo = Edt("src", "Configuration", "Configuration.mdo");

            Assert.Equal("Конфигурация", EdtLayout.ConfigurationName(mdo));
        }
        [Fact]
        public void КаталогБезМодулейОтдаётПустойСписок()
        {
            Assert.Empty(EdtLayout.ModulesIn(Edt("src", "Catalogs", "Справочник1", "Forms")));
        }
    }
}
