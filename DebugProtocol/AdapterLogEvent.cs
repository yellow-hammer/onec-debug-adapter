using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;

namespace Onec.DebugAdapter.DebugProtocol
{
    /// <summary>Диагностическое сообщение адаптера; отображение — на усмотрение клиента.</summary>
    class AdapterLogEvent : DebugEvent
    {
        public AdapterLogEvent(string message) : base("AdapterLog")
        {
            Message = message;
        }

        [JsonProperty("message")]
        public string Message { get; }
    }
}
