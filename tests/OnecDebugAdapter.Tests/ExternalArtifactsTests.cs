using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Поиск внешних обработок и отчётов в обоих форматах исходного кода.</summary>
    public class ExternalArtifactsTests
    {
        private static string Fixture(params string[] segments)
            => Path.Combine([AppContext.BaseDirectory, "fixtures", .. segments]);

        private static string[] Names(IEnumerable<string> descriptors)
            => descriptors.Select(descriptor => Path.GetFileNameWithoutExtension(descriptor)).OrderBy(name => name).ToArray();

        [Fact]
        public void КаталогСАртефактамиДаётОписанияОбоихФорматов()
        {
            var descriptors = ExternalArtifacts.Descriptors(Fixture("external"));

            Assert.Equal(
                new[] { "ТестоваяВнешняяОбработка", "ТестоваяВнешняяОбработка", "ТестовыйВнешнийОтчет" },
                Names(descriptors));
        }

        [Fact]
        public void ПроектEDTДаётОбработкиИОтчёты()
        {
            var descriptors = ExternalArtifacts.Descriptors(Fixture("external", "edt"));

            Assert.Equal(new[] { "ТестоваяВнешняяОбработка", "ТестовыйВнешнийОтчет" }, Names(descriptors));
            Assert.All(descriptors, descriptor => Assert.EndsWith(".mdo", descriptor));
        }

        [Fact]
        public void КаталогОбъектаБезОписанияНеДаётАртефакта()
        {
            var descriptors = ExternalArtifacts.Descriptors(Fixture("external", "designer", "ТестоваяВнешняяОбработка"));

            Assert.Empty(descriptors);
        }

        [Fact]
        public void ОписаниеВФорматеКонфигуратораЛежитРядомСКаталогомОбъекта()
        {
            var descriptors = ExternalArtifacts.Descriptors(Fixture("external", "designer"));

            Assert.Equal(new[] { "ТестоваяВнешняяОбработка" }, Names(descriptors));
            Assert.All(descriptors, descriptor => Assert.EndsWith(".xml", descriptor));
        }

        [Fact]
        public void ИсходныйКодКонфигурацииНеСчитаетсяВнешнимАртефактом()
        {
            Assert.Empty(ExternalArtifacts.Descriptors(Fixture("edt")));
            Assert.Empty(ExternalArtifacts.Descriptors(Fixture("designer")));
        }

        [Fact]
        public void НесуществующийКаталогДаётПустойСписок()
        {
            Assert.Empty(ExternalArtifacts.Descriptors(Fixture("external", "нет")));
        }
    }
}
