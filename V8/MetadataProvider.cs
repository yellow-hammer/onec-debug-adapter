using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Onec.DebugAdapter.Services;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Xml;
using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace Onec.DebugAdapter.V8
{
    public class MetadataProvider : IMetadataProvider
    {
        private readonly IDebugConfiguration _configuration;
        private readonly ConcurrentDictionary<string, (string Extension, string ObjectId, string PropertyId)> _modulesInfoByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string Extension, string ObjectId, string PropertyId), string> _pathsByModuleInfo = new();

        // Кросс-ОС отладка: модули дополнительно индексируются по пути относительно корня выгрузки,
        // корень клиента определяется по первому совпавшему запросу точек останова.
        private readonly ConcurrentDictionary<string, (string Extension, string ObjectId, string PropertyId)> _modulesInfoByRelPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string Extension, string ObjectId, string PropertyId), string> _relPathsByModuleInfo = new();
        private readonly ConcurrentDictionary<string, string> _clientRootsByExtension = new();
        private volatile bool _clientUsesBackslash = Path.DirectorySeparatorChar == '\\';

        // Модули внешних обработок/отчётов: при установке точек им нужны BslModuleType.ExtMdModule
        // и URL собранного файла (.epf/.erf); пустой URL — собранный файл не найден.
        private readonly ConcurrentDictionary<(string Extension, string ObjectId, string PropertyId), string> _externalModules = new();

        public MetadataProvider(IDebugConfiguration debugConfiguration)
        {
            _configuration = debugConfiguration;
        }

        public async Task Init(DebugProtocolClient client, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString();
            client.SendEvent(new ProgressStartEvent(id, "Чтение структуры конфигурации"));

			await FillMetadataCache(cancellationToken);

			client.SendEvent(new ProgressEndEvent(id));
		}

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            }
        }

        private static string ToForwardSlashes(string path)
            => path.Trim().Replace('\\', '/').TrimEnd('/');

        public string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
        {
            var key = (extension, objectId, propertyId);

            // Известен корень клиента — путь возвращаем в его формате.
            if (_clientRootsByExtension.TryGetValue(extension, out var clientRoot)
                && _relPathsByModuleInfo.TryGetValue(key, out var rel))
            {
                var clientPath = clientRoot + "/" + rel[(rel.IndexOf('/') + 1)..];
                return _clientUsesBackslash ? clientPath.Replace('/', '\\') : clientPath;
            }

            if (_pathsByModuleInfo.TryGetValue(key, out var path))
                return path;
            throw new KeyNotFoundException($"Модуль не найден в кэше метаданных: Extension={extension}, ObjectId={objectId}, PropertyId={propertyId}.");
        }

        public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default)
        {
            var normalized = NormalizePath(path);
            if (_modulesInfoByPath.TryGetValue(normalized, out var info))
                return info;

            // Абсолютный путь не совпал (другая ОС/машина) — резолв по относительному хвосту.
            var forward = ToForwardSlashes(path);
            foreach (var kv in _modulesInfoByRelPath)
            {
                if (!forward.EndsWith("/" + kv.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relWithoutRootName = kv.Key[(kv.Key.IndexOf('/') + 1)..];
                var clientRoot = forward[..^(relWithoutRootName.Length + 1)];
                _clientRootsByExtension[kv.Value.Extension] = clientRoot;
                _clientUsesBackslash = path.Contains('\\');
                _modulesInfoByPath.TryAdd(normalized, kv.Value);
                Log.Debug($"кросс-ОС пути: «{path}» сопоставлен по хвосту «{kv.Key}»; корень клиента: «{clientRoot}»");
                return kv.Value;
            }

            throw new KeyNotFoundException($"Путь к модулю не найден в структуре конфигурации: {path}. Убедитесь, что rootProject и extensions в launch.json указывают на выгрузку конфигурации, содержащую этот модуль.");
        }

        private static string GetPropertyId(string mdType, string moduleName)
        {
            return mdType switch
            {
                "CommonModules" or "WebServices" or "HTTPServices" => "d5963243-262e-4398-b4d7-fb16d06484f6",
                _ => moduleName switch
                {
                    "Module" => "32e087ab-1491-49b6-aba7-43571b41ac2b",
                    "CommandModule" => "078a6af8-d22c-4248-9c33-7e90075a3d2c",
                    "ObjectModule" => "a637f77f-3840-441d-a1c3-699c8c5cb7e0",
                    "ManagerModule" => "d1b64a2c-8078-4982-8190-8f81aefda192",
                    "RecordSetModule" => "9f36fd70-4bf4-47f6-b235-935f73aab43f",
                    "ValueManagerModule" => "3e58c91f-9aaa-4f42-8999-4baf33907b75",
                    "ManagedApplicationModule" => "d22e852a-cf8a-4f77-8ccb-3548e7792bea",
                    "SessionModule" => "9b7bbbae-9771-46f2-9e4d-2489e0ffc702",
                    "ExternalConnectionModule" => "a4a9c1e2-1e54-4c7f-af06-4ca341198fac",
                    "OrdinaryApplicationModule" => "a78d9ce3-4e0c-48d5-9863-ae7342eedf94",
                    _ => throw new NotImplementedException($"{mdType}\\{moduleName} is unknown module type")
                }
            };
        }

        private static string GetObjectId(string path)
        {
            var xml = new XmlDocument();
            xml.Load(path);

            var xPath = "/*[local-name()='MetaDataObject']";
            var typedNode = xml.SelectSingleNode(xPath)?.FirstChild;

            return typedNode!.Attributes!.GetNamedItem("uuid")!.Value!;
        }

        private async Task FillMetadataCache(CancellationToken cancellationToken)
        {
            var blockOptions = new ExecutionDataflowBlockOptions()
            {
                CancellationToken = cancellationToken,
                EnsureOrdered = false,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                BoundedCapacity = DataflowBlockOptions.Unbounded
            };

            var mdReaderBlock = new ActionBlock<(string Extension, string Path, string Root, string? ExternalUrl)>(args =>
            {
                var mdName = Path.GetFileNameWithoutExtension(args.Path);
                var mdPath = Path.Combine(Path.GetDirectoryName(args.Path)!, mdName);
                var mdType = Directory.GetParent(mdPath)!.Name;

                var mdXml = new XmlDocument();
                mdXml.Load(args.Path);

                var typedNode = mdXml.SelectSingleNode("/*[local-name()='MetaDataObject']")!.FirstChild!;
                var objectId = typedNode.Attributes!.GetNamedItem("uuid")!.Value!;

                var extPath = Path.Combine(mdPath, "Ext");
                if (Directory.Exists(extPath))
                    foreach (var moduleFile in Directory.EnumerateFiles(extPath, "*.bsl", SearchOption.AllDirectories))
                    {
                        var propertyId = GetPropertyId(mdType, Path.GetFileNameWithoutExtension(moduleFile));
                        CacheModule(moduleFile, args.Extension, objectId, propertyId, args.Root, args.ExternalUrl);
                    }

                var formsPath = Path.Combine(mdPath, "Forms");
                if (Directory.Exists(formsPath))
                    foreach (var formXmlFile in Directory.EnumerateFiles(formsPath, "*.xml"))
                    {
                        var formPath = Path.Combine(formsPath, Path.GetFileNameWithoutExtension(formXmlFile));
                        if (Directory.Exists(formPath))
                        {
                            var formModuleFile = Directory.EnumerateFiles(formPath, "*.bsl", SearchOption.AllDirectories).FirstOrDefault();
                            if (formModuleFile != null)
                            {
                                var propertyId = GetPropertyId(mdType, Path.GetFileNameWithoutExtension(formModuleFile));
                                CacheModule(formModuleFile, args.Extension, GetObjectId(formXmlFile), propertyId, args.Root, args.ExternalUrl);
                            }
                        }
                    }

                var commandsPath = Path.Combine(mdPath, "Commands");
                if (Directory.Exists(commandsPath))
                {
                    var commandNodes = typedNode.SelectNodes("./*[local-name()='ChildObjects']/*[local-name()='Command']")!;
                    foreach (XmlNode commandNode in commandNodes)
                    {
                        var commandObjectId = commandNode.Attributes!.GetNamedItem("uuid")!.Value!;
                        var commandName = commandNode.SelectSingleNode("./*[local-name()='Properties']/*[local-name()='Name']")!.InnerText;
                        var commandPath = Path.Combine(commandsPath, commandName);
                        if (Directory.Exists(commandPath))
                        {
                            var commandModuleFile = Directory.EnumerateFiles(commandPath, "*.bsl", SearchOption.AllDirectories).FirstOrDefault();
                            if (commandModuleFile != null)
                                // Захардкоженный идентификатор типа модуля формы
                                CacheModule(commandModuleFile, args.Extension, commandObjectId, GetPropertyId("", Path.GetFileNameWithoutExtension(commandModuleFile)), args.Root, args.ExternalUrl);
                        }
                    }
                }
            }, blockOptions);

            var rootReaderBlock = new ActionBlock<(string Extension, string Path)>(async args =>
            {
                var mdXml = new XmlDocument();
                mdXml.Load(Path.Combine(args.Path, "Configuration.xml"));

                var typedNode = mdXml.SelectSingleNode("/*[local-name()='MetaDataObject']")!.FirstChild!;
                var objectId = typedNode.Attributes!.GetNamedItem("uuid")!.Value!;

                var extPath = Path.Combine(args.Path, "Ext");
                if (Directory.Exists(extPath))
                    foreach (var moduleFile in Directory.EnumerateFiles(extPath, "*.bsl"))
                    {
                        var propertyId = GetPropertyId("", Path.GetFileNameWithoutExtension(moduleFile));
                        CacheModule(moduleFile, args.Extension, objectId, propertyId, args.Path, null);
                    }

                var rootMdfolders = Directory.GetDirectories(args.Path);

                foreach (var rootMdFolder in rootMdfolders)
                {
                    if (new DirectoryInfo(rootMdFolder).Name == "Ext")
                        continue;

                    var xmlFiles = Directory.GetFiles(rootMdFolder, "*.xml");

                    foreach (var xmlFile in xmlFiles)
                        await mdReaderBlock.SendAsync((args.Extension, xmlFile, args.Path, null), cancellationToken).ConfigureAwait(false);
                }
            }, blockOptions);

            _ = rootReaderBlock.Completion.ContinueWith(delegate { mdReaderBlock.Complete(); }, cancellationToken).ConfigureAwait(false);

            await rootReaderBlock.SendAsync((string.Empty, _configuration.RootProject));
            foreach(var kv in _configuration.Extensions)
                await rootReaderBlock.SendAsync((kv.Key, kv.Value));

            // Внешние обработки/отчёты: каталог артефакта содержит <Имя>.xml той же структуры,
            // что и объект конфигурации, — обрабатывается тем же конвейером. URL собранного файла
            // обязателен для точек останова (сервер адресует внешние модули по нему).
            foreach (var sourceDir in _configuration.ExternalSources)
            {
                var artifactName = Path.GetFileName(sourceDir.TrimEnd('\\', '/'));
                var rootXml = Path.Combine(sourceDir, artifactName + ".xml");
                if (!File.Exists(rootXml))
                    rootXml = Directory.EnumerateFiles(sourceDir, "*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "";
                if (rootXml.Length == 0)
                    continue;

                var buildFile = _configuration.ExternalBuildFile(artifactName);
                var url = buildFile == null ? "" : "file://" + ToForwardSlashes(buildFile);
                if (buildFile == null)
                    Log.Debug($"внешний артефакт «{artifactName}»: собранный файл не найден (externalDataProcessorsBuilds/externalReportsBuilds) — точки в его модулях работать не будут");

                await mdReaderBlock.SendAsync((string.Empty, rootXml, sourceDir, url), cancellationToken).ConfigureAwait(false);
            }

            rootReaderBlock.Complete();

            await mdReaderBlock.Completion;
        }

        public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info)
            => _externalModules.ContainsKey(info);

        /// <summary>Путь модуля на машине адаптера (для чтения исходника), независимо от кросс-ОС резолва.</summary>
        public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info)
            => _pathsByModuleInfo.TryGetValue(info, out var path) ? path : null;

        /// <summary>
        /// Модули расширений с тем же путём внутри выгрузки, что у модуля базовой конфигурации, —
        /// кандидаты на зеркалирование точек останова при заместителях («Вместо»/«После»/«Перед»).
        /// </summary>
        public IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info)
        {
            if (!_relPathsByModuleInfo.TryGetValue(info, out var relKey))
                yield break;

            var suffix = relKey[(relKey.IndexOf('/') + 1)..];
            foreach (var kv in _modulesInfoByRelPath)
            {
                if (kv.Value.Extension.Length == 0 || kv.Value.Equals(info) || IsExternalModule(kv.Value))
                    continue;
                if (kv.Key.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
                    yield return kv.Value;
            }
        }

        public string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info)
            => _externalModules.TryGetValue(info, out var url) ? url : string.Empty;

        private void CacheModule(string path, string extension, string objectId, string propertyId, string root, string? externalUrl)
        {
            var normalizedPath = NormalizePath(path);
            var info = (extension, objectId, propertyId);
            _modulesInfoByPath.TryAdd(normalizedPath, info);
            _pathsByModuleInfo.TryAdd(info, normalizedPath);
            if (externalUrl != null)
                _externalModules.TryAdd(info, externalUrl);

            // Относительный индекс для кросс-ОС резолва: «<имя каталога корня>/<путь внутри выгрузки>».
            var normalizedRoot = NormalizePath(root);
            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relKey = Path.GetFileName(normalizedRoot) + "/" + ToForwardSlashes(normalizedPath[normalizedRoot.Length..].TrimStart('\\', '/'));
                _modulesInfoByRelPath.TryAdd(relKey, info);
                _relPathsByModuleInfo.TryAdd(info, relKey);
            }
        }
    }
}
