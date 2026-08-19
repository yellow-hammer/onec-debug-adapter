using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Onec.DebugAdapter.DebugProtocol;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;
using System.Collections.Concurrent;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Onec.DebugAdapter.Services
{
    /// <summary>
    /// Замер производительности: включает режим замера на сервере отладки, накапливает результаты
    /// от предметов отладки и отправляет клиенту агрегированный снимок событием MeasureResults.
    /// </summary>
    public class MeasureManager : IMeasureManager
    {
        private static readonly ConcurrentDictionary<string, XmlSerializer> Serializers = new();

        private readonly IDebugConfiguration _configuration;
        private readonly IDebugServerClient _debugServerClient;
        private readonly IDebugServerListener _debugServerListener;
        private readonly IMetadataProvider _metadataProvider;

        private DebugProtocolClient? _client;
        private CancellationToken _cancellation;

        private readonly object _lock = new();
        private readonly Dictionary<(string Extension, string ObjectId, string PropertyId), Dictionary<int, LineMeasure>> _measures = new();
        private double _totalSeconds;
        private volatile bool _active;

        private sealed class LineMeasure
        {
            public long Count;
            public double Seconds;
            public double OwnSeconds;
            public bool ServerCall;
        }

        public MeasureManager(
            IDebugConfiguration configuration,
            IDebugServerClient debugServerClient,
            IDebugServerListener debugServerListener,
            IMetadataProvider metadataProvider)
        {
            _configuration = configuration;
            _debugServerClient = debugServerClient;
            _debugServerListener = debugServerListener;
            _metadataProvider = metadataProvider;
        }

        public void Init(DebugProtocolClient client, CancellationToken cancellationToken)
        {
            _client = client;
            _cancellation = cancellationToken;
            _debugServerListener.MeasureResults += MeasureResultsHandler;
        }

        public async Task SetMeasureMode(bool enabled)
        {
            if (enabled)
                lock (_lock)
                {
                    _measures.Clear();
                    _totalSeconds = 0;
                }

            var request = _configuration.CreateRequest<RdbgSetMeasureModeRequest>();
            request.MeasureModeSeanceId = (enabled ? Guid.NewGuid() : Guid.Empty).ToString();

            await _debugServerClient.SetMeasureMode(request, _cancellation);
            _active = enabled;
            Log.Debug($"замер производительности {(enabled ? "включён" : "выключен")}");
        }

        /// <summary>Режим замера живёт только по явному запросу — при завершении сессии снимаем его на сервере.</summary>
        public async Task DisableOnDisconnect()
        {
            if (!_active)
                return;

            try
            {
                await SetMeasureMode(false);
            }
            catch
            {
                // Сессия завершается — сервер мог уже стать недоступен.
            }
        }

        private void MeasureResultsHandler(object? sender, MeasureResultsEventArgs e)
        {
            try
            {
                var measure = e.Info.Measure ?? Deserialize(e.Info.MeasureStr);
                if (measure == null || !measure.ModuleDataSpecified)
                    return;

                var frequency = measure.PerformanceFrequency != 0 ? (double)measure.PerformanceFrequency : 1;

                MeasureResultsEvent snapshot;
                lock (_lock)
                {
                    _totalSeconds += (double)measure.TotalDurability / frequency;

                    foreach (var module in measure.ModuleData)
                    {
                        var key = (module.ModuleId.ExtensionName ?? "", module.ModuleId.ObjectId, module.ModuleId.PropertyId);
                        if (!_measures.TryGetValue(key, out var lines))
                            _measures[key] = lines = new Dictionary<int, LineMeasure>();

                        foreach (var line in module.LineInfo)
                        {
                            if (line.Frequency == 0)
                                continue;

                            var lineNo = (int)line.LineNo;
                            if (!lines.TryGetValue(lineNo, out var stat))
                                lines[lineNo] = stat = new LineMeasure();

                            stat.Count += (long)line.Frequency;
                            stat.Seconds += (double)line.Durability / frequency;
                            stat.OwnSeconds += (double)line.PureDurability / frequency;
                            stat.ServerCall |= line.ServerCallSignal != 0;
                        }
                    }

                    snapshot = BuildSnapshot();
                }

                Log.Debug($"замер: получены результаты, модулей в снимке — {snapshot.Modules.Count}");
                _client?.SendEvent(snapshot);
            }
            catch (System.Exception ex)
            {
                _client?.SendError(ex);
            }
        }

        private MeasureResultsEvent BuildSnapshot()
        {
            var modules = new List<MeasureModuleResult>();

            foreach (var (key, lines) in _measures)
            {
                // Модули вне исходного кода проекта (например, недоступных расширений) пропускаем.
                string path;
                try { path = _metadataProvider.ModulePathByInfo(key.Extension, key.ObjectId, key.PropertyId, _cancellation); }
                catch { continue; }

                modules.Add(new MeasureModuleResult()
                {
                    Path = path,
                    Lines = lines.OrderBy(l => l.Key).Select(l => new MeasureLineResult()
                    {
                        Line = l.Key,
                        Count = l.Value.Count,
                        Seconds = l.Value.Seconds,
                        OwnSeconds = l.Value.OwnSeconds,
                        ServerCall = l.Value.ServerCall
                    }).ToList()
                });
            }

            return new MeasureResultsEvent(_totalSeconds, modules);
        }

        /// <summary>Результат замера в base64 (measureStr) — XML PerformanceInfoMain.</summary>
        private static PerformanceInfoMain? Deserialize(byte[]? data)
        {
            if (data == null || data.Length == 0)
                return null;

            using var stream = new MemoryStream(data);
            var document = XDocument.Load(stream);
            if (document.Root == null)
                return null;

            var serializer = Serializers.GetOrAdd(
                document.Root.Name.ToString(),
                _ => new XmlSerializer(
                    typeof(PerformanceInfoMain),
                    new XmlRootAttribute(document.Root.Name.LocalName) { Namespace = document.Root.Name.NamespaceName }));

            return (PerformanceInfoMain?)serializer.Deserialize(document.CreateReader());
        }
    }
}
