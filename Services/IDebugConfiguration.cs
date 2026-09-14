using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.V8;

namespace Onec.DebugAdapter.Services
{
    public interface IDebugConfiguration
    {
        Task Initialization { get; }

		InfoBaseItem InfoBase { get; }
        bool IsFileInfoBase { get; }
        // Сервер отладки запускает сам адаптер: только при запуске файловой ИБ.
        // При присоединении сервер уже работает, например внутри автономного сервера.
        bool OwnsDebugServer { get; }
        string InfoBaseName { get; }
        string PlatformBin { get; }
        string DebuggerID { get; }
        string DebugServerHost { get; }
        int DebugServerPort { get; }
        string RootProject { get; }
        IReadOnlyDictionary<string, string> Extensions { get; }
        /// <summary>Описания внешних обработок и отчётов: <c>&lt;Имя&gt;.xml</c> или <c>&lt;Имя&gt;.mdo</c>.</summary>
        IReadOnlyList<string> ExternalSources { get; }
        string? ExternalBuildFile(string artifactName);
        DebugTargetType[] InitialTargetTypes { get; }
        int PollMinDelayMs { get; }
        int PollMaxDelayMs { get; }
        int CalcWaitingTimeMs { get; }
        IReadOnlyList<int> VariablesRetryDelaysMs { get; }
        bool DiagnosticLogging { get; }
        string User { get; }
        string Password { get; }

        // Порт своего сервера отладки выбирается на лету, поэтому требуется инжект в конфигурацию отладки
        void SetDebugServerPort(int port);

        T CreateRequest<T>() where T : RDbgBaseRequest, new();
        T CreateRequest<T>(Action<T> factory) where T : RDbgBaseRequest, new();
        Task Init(Dictionary<string, JToken> arguments);
    }
}