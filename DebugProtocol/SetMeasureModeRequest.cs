using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;

namespace Onec.DebugAdapter.DebugProtocol
{
    /// <summary>Включение/выключение замера производительности клиентом.</summary>
    public class SetMeasureModeRequest : DebugRequestWithResponse<SetMeasureModeArguments, SetMeasureModeResponse>
    {
        public SetMeasureModeRequest() : base("SetMeasureModeRequest") { }
    }

    public class SetMeasureModeArguments
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }
    }

    public class SetMeasureModeResponse : ResponseBody
    {
    }
}
