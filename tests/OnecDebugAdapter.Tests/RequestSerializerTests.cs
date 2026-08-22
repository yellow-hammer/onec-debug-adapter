using System.Xml;
using Onec.DebugAdapter.DebugServer;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Тело запроса к серверу отладки: XML должен заканчиваться корневым элементом.
    ///
    /// Ранее сериализатор отдавал весь внутренний буфер MemoryStream вместе с
    /// незаполненным хвостом, и нулевые байты уезжали в запрос. Сервер отвечал
    /// «Extra content at the end of the document» и отклонял его целиком. Ловилось
    /// это не всегда: пока длина XML совпадала с размером буфера, хвоста не было.
    /// </summary>
    public class RequestSerializerTests
    {
        private static RdbgSetBreakpointsRequest RequestWith(int modules)
        {
            var request = new RdbgSetBreakpointsRequest();
            for (var i = 0; i < modules; i++)
                request.BpWorkspace.Add(new ModuleBpInfoInternal
                {
                    Id = new BslModuleIdInternal
                    {
                        Type = BslModuleType.ExtMdModule,
                        Url = $"file://C:/Users/ikarl/git/ssl_3_1/build/out/epf/Обработка{i}.epf",
                        ExtensionName = "",
                        ObjectId = "b41d8e07-5c26-4a93-9e08-7f6d3b12ac54",
                        PropertyId = "32e087ab-1491-49b6-aba7-43571b41ac2b",
                    },
                });
            return request;
        }

        /// <summary>
        /// Хвост появляется не на всякой длине: до восьми модулей буфер совпадал
        /// с содержимым, и запрос проходил. Поэтому проверяется набор размеров.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(12)]
        [InlineData(30)]
        public void ТелоЗапросаНеСодержитХвостаПослеКорня(int modules)
        {
            var xml = new RequestSerializer().Serialize(RequestWith(modules));

            Assert.NotNull(xml);
            Assert.DoesNotContain('\0', xml);
            Assert.EndsWith("</request>", xml.TrimEnd());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(30)]
        public void ТелоЗапросаРазбираетсяКакXML(int modules)
        {
            var xml = new RequestSerializer().Serialize(RequestWith(modules));

            var document = new XmlDocument();
            var exception = Record.Exception(() => document.LoadXml(xml!));

            Assert.Null(exception);
            Assert.Equal("request", document.DocumentElement!.Name);
        }
    }
}
