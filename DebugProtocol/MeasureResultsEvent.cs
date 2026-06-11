using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;

namespace Onec.DebugAdapter.DebugProtocol
{
    /// <summary>
    /// Накопленные результаты замера производительности. Отправляется при каждой порции данных
    /// от предметов отладки — клиент отображает последнее состояние целиком.
    /// </summary>
    class MeasureResultsEvent : DebugEvent
    {
        public MeasureResultsEvent(double totalSeconds, List<MeasureModuleResult> modules) : base("MeasureResults")
        {
            TotalSeconds = totalSeconds;
            Modules = modules;
        }

        [JsonProperty("totalSeconds")]
        public double TotalSeconds { get; }

        [JsonProperty("modules")]
        public List<MeasureModuleResult> Modules { get; }
    }

    class MeasureModuleResult
    {
        [JsonProperty("path")]
        public string Path { get; set; } = "";

        [JsonProperty("lines")]
        public List<MeasureLineResult> Lines { get; set; } = new();
    }

    class MeasureLineResult
    {
        [JsonProperty("line")]
        public int Line { get; set; }

        [JsonProperty("count")]
        public long Count { get; set; }

        [JsonProperty("seconds")]
        public double Seconds { get; set; }

        [JsonProperty("ownSeconds")]
        public double OwnSeconds { get; set; }

        [JsonProperty("serverCall")]
        public bool ServerCall { get; set; }
    }
}
