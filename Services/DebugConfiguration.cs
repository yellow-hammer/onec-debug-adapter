using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;
using Newtonsoft.Json.Linq;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Onec.DebugAdapter.Services
{
    public class DebugConfiguration : IDebugConfiguration
    {
        private readonly TaskCompletionSource _tcs = new();
        public Task Initialization => _tcs.Task;

        public InfoBaseItem InfoBase { get; private set; } = null!;
        public bool IsFileInfoBase { get; private set; } = false;
        public string InfoBaseName { get; private set; } = string.Empty;
        public string PlatformBin { get; private set; } = string.Empty;
        public string DebugServerHost { get; private set; } = string.Empty;
        public int DebugServerPort { get; private set; } = 1550;
        public string RootProject { get; private set; } = string.Empty;
        public string DebuggerID { get; private set; } = string.Empty;

        private readonly Dictionary<string, string> _extensions = new();
        public IReadOnlyDictionary<string, string> Extensions => _extensions;

        // Каталоги исходников внешних обработок/отчётов (по одному на артефакт).
        private readonly List<string> _externalSources = new();
        public IReadOnlyList<string> ExternalSources => _externalSources;

        // Собранные .epf/.erf: имя артефакта → файл. Точкам останова во внешних модулях
        // нужен URL файла — без него сервер отладки отвергает запрос.
        private readonly Dictionary<string, string> _externalBuildFiles = new(StringComparer.OrdinalIgnoreCase);
        public string? ExternalBuildFile(string artifactName)
            => _externalBuildFiles.TryGetValue(artifactName, out var file) ? file : null;

        private static List<int>? GetValueAsIntArray(Dictionary<string, JToken> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var token) || token is not JArray array)
                return null;
            var result = new List<int>();
            foreach (var item in array)
                if (item.Type is JTokenType.Integer or JTokenType.Float)
                    result.Add(Math.Clamp((int)item, 0, 10000));
            return result;
        }

        private void InitExternalBuilds(Dictionary<string, JToken> arguments, string key)
        {
            var dirs = arguments.GetValueAsStringArray(key);
            if (dirs == null)
                return;

            foreach (var dir in dirs.Where(Directory.Exists))
                foreach (var pattern in new[] { "*.epf", "*.erf" })
                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                        _externalBuildFiles.TryAdd(Path.GetFileNameWithoutExtension(file), Path.GetFullPath(file));
        }

        // Путь из конфигурации запуска может указывать и на каталог артефакта (содержит <Имя>.xml),
        // и на корень с несколькими артефактами — раскладываем до каталогов артефактов.
        private void InitExternalSources(Dictionary<string, JToken> arguments, string key)
        {
            var paths = arguments.GetValueAsStringArray(key);
            if (paths == null)
                return;

            foreach (var path in paths.Where(Directory.Exists))
            {
                if (File.Exists(Path.Combine(path, Path.GetFileName(path.TrimEnd('\\', '/')) + ".xml")))
                    _externalSources.Add(path);
                else
                    _externalSources.AddRange(Directory.EnumerateDirectories(path)
                        .Where(dir => File.Exists(Path.Combine(dir, Path.GetFileName(dir) + ".xml"))));
            }
        }
        public DebugTargetType[] InitialTargetTypes { get; private set; } = System.Array.Empty<DebugTargetType>();

        // Границы адаптивного интервала опроса сервера отладки (PingDebugUI).
        public int PollMinDelayMs { get; private set; } = 25;
        public int PollMaxDelayMs { get; private set; } = 200;

        // Время ожидания результата вычислений сервером (calcWaitingTime RDBG) — на медленных
        // серверах малое значение даёт пустые ответы evalExpr/evalLocalVariables.
        public int CalcWaitingTimeMs { get; private set; } = 100;

        // Паузы повторов при пустом ответе сервера на запрос локальных переменных.
        public IReadOnlyList<int> VariablesRetryDelaysMs { get; private set; } = new[] { 50, 100, 150 };

        // Диагностический вывод адаптера (флаг trace конфигурации запуска).
        public bool DiagnosticLogging { get; private set; }

        // Учётные данные автовхода в ИБ; пусто — стандартное окно аутентификации.
        public string User { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;

        public async Task Init(Dictionary<string, JToken> arguments)
        {
            await InitInfoBase(arguments);
            InitPlatformBin(arguments);
            InitExtension(arguments);
            InitInitialTargetTypes(arguments);

            PollMinDelayMs = Math.Max(1, arguments.GetValueAsInt("debugPollMinDelayMs") ?? 25);
            PollMaxDelayMs = Math.Max(PollMinDelayMs, arguments.GetValueAsInt("debugPollMaxDelayMs") ?? 200);
            CalcWaitingTimeMs = Math.Clamp(arguments.GetValueAsInt("calcWaitingTimeMs") ?? 100, 25, 5000);
            var retryDelays = GetValueAsIntArray(arguments, "variablesRetryDelaysMs");
            if (retryDelays is { Count: > 0 })
                VariablesRetryDelaysMs = retryDelays;
            DiagnosticLogging = arguments.GetValueAsBool("trace") ?? false;
            User = arguments.GetValueAsString("user");
            Password = arguments.GetValueAsString("password");

            InitExternalSources(arguments, "externalFilesSrc");
            InitExternalBuilds(arguments, "externalFilesBuilds");

            DebugServerHost = arguments.GetValueAsString("debugServerHost");
            
            if (!IsFileInfoBase)
                DebugServerPort = arguments.GetValueAsInt("debugServerPort") ?? 1550;

            RootProject = arguments.GetValueAsString("rootProject");
            DebuggerID = Guid.NewGuid().ToString();

            if (!IsFileInfoBase)
                _tcs.SetResult();
        }

		public void SetDebugServerPort(int port)
        {
			DebugServerPort = port;

			if (IsFileInfoBase)
				_tcs.SetResult();
		}

		private Task InitInfoBase(Dictionary<string, JToken> arguments)
        {
            var connectionString = arguments.GetValueAsString("connectionString") ?? "";
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new System.Exception("Не задана строка подключения к информационной базе (ожидается из env.json: default['--ibconnection'])");

            connectionString = connectionString.Trim();
            if (!connectionString.StartsWith("/F", StringComparison.OrdinalIgnoreCase) && !connectionString.StartsWith("/S", StringComparison.OrdinalIgnoreCase))
                throw new System.Exception($"Строка подключения должна начинаться с /F (файловая ИБ) или /S (серверная ИБ): {connectionString}");

            var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Connect"] = connectionString };
            string name;
            if (connectionString.StartsWith("/F", StringComparison.OrdinalIgnoreCase))
            {
                IsFileInfoBase = true;
                InfoBaseName = "DefAlias";
                name = "DefAlias";
            }
            else
            {
                var afterSlash = connectionString.Length > 2 ? connectionString.Substring(2) : "";
                var lastBackslash = afterSlash.LastIndexOf('\\');
                InfoBaseName = lastBackslash >= 0 && lastBackslash < afterSlash.Length - 1
                    ? afterSlash.Substring(lastBackslash + 1)
                    : (string.IsNullOrEmpty(afterSlash) ? "ServerBase" : afterSlash);
                name = InfoBaseName;
            }

            InfoBase = new InfoBaseItem(name, properties);
            return Task.CompletedTask;
        }

        private void InitPlatformBin(Dictionary<string, JToken> arguments)
        {
            if (arguments.GetValueAsString("request") == "launch")
            {
                var platformPath = arguments.GetValueAsString("platformPath");
                if (!Directory.Exists(platformPath))
                    throw new System.Exception("Каталог с версиями платформы 1С не найден");

                var versionFolders = Directory
                    .GetDirectories(platformPath)
                    .Select(c => new { new DirectoryInfo(c).Name, PlatformPath = c })
                    .Where(c => Regex.IsMatch(c.Name, @"^\d+\.\d+\.\d+\.\d+$"))
                    .OrderByDescending(c => c.Name)
                    .ToList();

                if (versionFolders.Count == 0)
                    throw new System.Exception("Не найдены установленные версии платформы");
                
                var binFolder = Environment.OSVersion.Platform switch
                {
                    PlatformID.Win32NT => "bin",
                    _ => ""
                };

                var platformVersion = arguments.GetValueAsString("platformVersion") ?? string.Empty;
                if (string.IsNullOrEmpty(platformVersion) || platformVersion.ToUpper() == "LATEST")
                    PlatformBin = Path.Combine(versionFolders.First().PlatformPath, binFolder);
                else
                {
                    var versionItem = versionFolders.FirstOrDefault(c => c.Name == platformVersion);
                    if (versionItem == null)
                        throw new System.Exception($"Указанная версия платформы ({platformVersion}) не найдена"); 
                    
                    PlatformBin = Path.Combine(versionItem.PlatformPath, binFolder);
                }
            }
        }

        private void InitInitialTargetTypes(Dictionary<string, JToken> arguments)
        {
            var types = arguments.GetValueAsStringArray("autoAttachTypes");

            InitialTargetTypes = types switch
            {
                null => System.Array.Empty<DebugTargetType>(),
                _ => types.ToList().Select(c => Enum.Parse<DebugTargetType>(c)).ToArray()
            };
        }

        private void InitExtension(Dictionary<string, JToken> arguments)
        {
            var paths = arguments.GetValueAsStringArray("extensions");
            if (paths == null)
                return;

            foreach (var path in paths)
            {
                var extensionName = ExtensionName(path);
                if (string.IsNullOrEmpty(extensionName))
                {
                    Log.Debug($"расширение «{path}» пропущено: исходного кода расширения там нет");
                    continue;
                }

                _extensions[extensionName] = path;
            }
        }

        /// <summary>Имя расширения из его исходного кода; пусто - каталог не содержит расширения.</summary>
        private static string ExtensionName(string path)
        {
            var designerFile = Path.Join(path, "Configuration.xml");
            if (File.Exists(designerFile))
            {
                var xml = new XmlDocument();
                xml.Load(designerFile);
                var xPath = "/*[local-name()='MetaDataObject']/*[local-name()='Configuration']/*[local-name()='Properties']/*[local-name()='Name']";
                return xml.SelectSingleNode(xPath)?.InnerText ?? string.Empty;
            }

            var edtRoot = EdtLayout.FindSourcesRoot(path);
            if (edtRoot != null)
                return EdtLayout.ConfigurationName(Path.Combine(edtRoot, "Configuration", "Configuration.mdo"));

            return string.Empty;
        }

        public T CreateRequest<T>() where T : RDbgBaseRequest, new()
        {
            var request = new T()
            {
                IdOfDebuggerUi = DebuggerID,
                InfoBaseAlias = InfoBaseName
            };

            return request;
        }

        public T CreateRequest<T>(Action<T> factory) where T : RDbgBaseRequest, new()
        {
            var request = CreateRequest<T>();
            factory.Invoke(request);

            return request;
        }
	}
}
