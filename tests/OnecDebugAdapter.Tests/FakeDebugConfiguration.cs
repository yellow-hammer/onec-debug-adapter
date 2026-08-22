using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Конфигурация отладки с заданными корнями исходников.</summary>
    internal sealed class FakeDebugConfiguration(
        string rootProject,
        (string Name, string Path)[] extensions,
        IReadOnlyList<string>? externalSources = null,
        Func<string, string?>? externalBuildFile = null,
        int debugServerPort = 1550) : IDebugConfiguration
    {
        public Task Initialization => Task.CompletedTask;
        public InfoBaseItem InfoBase => new("test", new Dictionary<string, string?>());
        public bool IsFileInfoBase => true;
        public string InfoBaseName => "test";
        public string PlatformBin => string.Empty;
        public string DebuggerID => "test";
        public string DebugServerHost => "localhost";
        public int DebugServerPort => debugServerPort;
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
