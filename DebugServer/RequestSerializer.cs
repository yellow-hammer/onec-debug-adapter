using RestSharp;
using RestSharp.Serializers;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Onec.DebugAdapter.DebugServer
{
    internal class RequestSerializer : IRestSerializer, ISerializer, IDeserializer
    {
        private readonly XmlRootAttribute rootElement = new("request");

        public string? Serialize(object? obj)
        {
            if (obj == null) 
                return null;

            using var mStream = new MemoryStream();
            var serializer = new XmlSerializer(obj.GetType(), rootElement);
            serializer.Serialize(mStream, obj);

            // ToArray, а не GetBuffer: последний отдаёт весь внутренний буфер вместе
            // с незаполненным хвостом, и нулевые байты уезжают в тело запроса. Сервер
            // видит их как содержимое после корневого элемента и отвечает 400.
            return Encoding.UTF8.GetString(mStream.ToArray());
        }

        public string? Serialize(Parameter bodyParameter) 
            => Serialize(bodyParameter.Value);

        public T? Deserialize<T>(RestResponse response)
        {
            if (response.RawBytes is null)
                return default;

            using var mStream = new MemoryStream(response.RawBytes);
            using var reader = XmlReader.Create(mStream);


            var rootElement = new XmlRootAttribute("response")
            {
                Namespace = "http://v8.1c.ru/8.3/debugger/debugBaseData"
            };

            return (T?)new XmlSerializer(typeof(T), rootElement).Deserialize(reader);
        }

        public ContentType ContentType { get; set; } = ContentType.Xml;

        public ISerializer Serializer => this;
        public IDeserializer Deserializer => this;
        public DataFormat DataFormat => DataFormat.Xml;
        public string[] AcceptedContentTypes => ContentType.XmlAccept;
        public SupportsContentType SupportsContentType
            => contentType => contentType.Value.EndsWith("xml", StringComparison.InvariantCultureIgnoreCase);
    }
}
