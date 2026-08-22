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

        // Кросс-ОС отладка: модули дополнительно индексируются по пути относительно корня исходного кода,
        // корень клиента определяется по первому совпавшему запросу точек останова.
        private readonly ConcurrentDictionary<string, (string Extension, string ObjectId, string PropertyId)> _modulesInfoByRelPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string Extension, string ObjectId, string PropertyId), string> _relPathsByModuleInfo = new();
        private readonly ConcurrentDictionary<string, string> _clientRootsByExtension = new();
        private volatile bool _clientUsesBackslash = Path.DirectorySeparatorChar == '\\';

        // Модули внешних обработок/отчётов: при установке точек им нужны BslModuleType.ExtMdModule
        // и URL собранного файла (.epf/.erf); пустой URL — собранный файл не найден.
        private readonly ConcurrentDictionary<(string Extension, string ObjectId, string PropertyId), string> _externalModules = new();

        // Копии обработок сохраняют uuid оригинала, поэтому тройка идентификаторов
        // у них совпадает. Для внешних модулей ключом служит то, что уникально:
        // путь исходника при установке точек и URL собранного файла при остановке.
        private readonly ConcurrentDictionary<string, string> _externalUrlsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string Url, string PropertyId), string> _pathsByExternalUrl = new();

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

        private static string ToForwardSlashes(string path)
            => path.Trim().Replace('\\', '/').TrimEnd('/');

        public string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
        {
            var key = (extension, objectId, propertyId);

            // Известен корень клиента — путь возвращаем в его формате.
            if (_clientRootsByExtension.TryGetValue(extension, out var clientRoot)
                && _relPathsByModuleInfo.TryGetValue(key, out var rel))
            {
                var clientPath = clientRoot + "/" + rel[(rel.IndexOf('/') + 1)..];
                return _clientUsesBackslash ? clientPath.Replace('/', '\\') : clientPath;
            }

            return _pathsByModuleInfo.TryGetValue(key, out var path) ? path : null;
        }

        public string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
            => TryModulePathByInfo(extension, objectId, propertyId, cancellationToken)
               ?? throw new KeyNotFoundException($"Модуль не найден в кэше метаданных: Extension={extension}, ObjectId={objectId}, PropertyId={propertyId}.");

        public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default)
        {
            var resolved = SourcePath.Resolve(path);
            if (!string.Equals(resolved, path, StringComparison.Ordinal))
                Log.Debug($"путь исходника: «{path}» → «{resolved}»");
            path = resolved;

            var normalized = SourcePath.Normalize(path);
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

            throw new KeyNotFoundException($"Путь к модулю не найден в структуре конфигурации: {path}. Убедитесь, что rootProject и extensions в launch.json указывают на каталог исходного кода, содержащий этот модуль.");
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

        internal async Task FillMetadataCache(CancellationToken cancellationToken)
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

            var edtReaderBlock = new ActionBlock<(string Extension, string MdType, string ObjectDir, string Root, string? ExternalUrl)>(args =>
            {
                ReadEdtObject(args.Extension, args.Root, args.MdType, args.ObjectDir, args.ExternalUrl);
            }, blockOptions);

            var rootReaderBlock = new ActionBlock<(string Extension, string Path)>(async args =>
            {
                var edtRoot = EdtLayout.FindSourcesRoot(args.Path);
                if (edtRoot != null)
                {
                    await SendEdtObjects(args.Extension, edtRoot, edtReaderBlock, cancellationToken).ConfigureAwait(false);
                    return;
                }

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

            // Внешние обработки и отчёты: структура артефакта та же, что у объекта конфигурации,
            // поэтому оба формата читаются теми же блоками. URL собранного файла обязателен для
            // точек останова (сервер адресует внешние модули по нему).
            foreach (var descriptor in _configuration.ExternalSources)
            {
                var artifactName = Path.GetFileNameWithoutExtension(descriptor);
                var artifactDir = Path.GetDirectoryName(descriptor)!;

                var buildFile = _configuration.ExternalBuildFile(artifactName);
                var url = buildFile == null ? "" : "file://" + ToForwardSlashes(buildFile);
                if (buildFile == null)
                    Log.Debug($"внешний артефакт «{artifactName}»: собранный файл не найден (externalFilesBuilds) — точки в его модулях работать не будут");

                if (descriptor.EndsWith(".mdo", StringComparison.OrdinalIgnoreCase))
                {
                    var mdType = Directory.GetParent(artifactDir)!.Name;
                    await edtReaderBlock.SendAsync((string.Empty, mdType, artifactDir, artifactDir, url), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await mdReaderBlock.SendAsync((string.Empty, descriptor, artifactDir, url), cancellationToken).ConfigureAwait(false);
            }

            rootReaderBlock.Complete();

            await mdReaderBlock.Completion;
            edtReaderBlock.Complete();
            await edtReaderBlock.Completion;
        }

        public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info)
            => _externalModules.ContainsKey(info);

        /// <summary>Путь модуля на машине адаптера (для чтения исходника), независимо от кросс-ОС резолва.</summary>
        public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info)
            => _pathsByModuleInfo.TryGetValue(info, out var path) ? path : null;

        /// <summary>
        /// Модули расширений с тем же путём внутри исходного кода, что у модуля базовой конфигурации, —
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

        /// <summary>
        /// URL собранного файла по пути исходника: у копий обработок uuid общий.
        /// Путь проходит тот же резолв, что и поиск модуля, иначе Git-URI и путь
        /// с другой машины не совпадут. Пусто — сопоставить не удалось.
        /// </summary>
        public string ExternalModuleUrlByPath(string path)
            => _externalUrlsByPath.TryGetValue(SourcePath.Canonical(path), out var url)
                ? url
                : string.Empty;

        /// <summary>Путь исходника по URL собранного файла и свойству модуля.</summary>
        public string? TryModulePathByExternalUrl(string url, string propertyId)
            => _pathsByExternalUrl.TryGetValue((url, propertyId), out var path) ? path : null;

        /// <summary>Раздаёт объекты EDT читателям: разбор mdo идёт параллельно, как у формата конфигуратора.</summary>
        private async Task SendEdtObjects(
            string extension,
            string sourcesRoot,
            ITargetBlock<(string Extension, string MdType, string ObjectDir, string Root, string? ExternalUrl)> reader,
            CancellationToken cancellationToken)
        {
            Log.Debug($"формат EDT: {sourcesRoot}");

            var configurationDir = Path.Combine(sourcesRoot, "Configuration");
            var configurationId = EdtLayout.ObjectId(Path.Combine(configurationDir, "Configuration.mdo"));
            foreach (var moduleFile in EdtLayout.ModulesIn(configurationDir))
                CacheModule(moduleFile, extension, configurationId, GetPropertyId("", Path.GetFileNameWithoutExtension(moduleFile)), sourcesRoot, null);

            foreach (var typeDir in Directory.GetDirectories(sourcesRoot))
            {
                var mdType = new DirectoryInfo(typeDir).Name;
                if (mdType == "Configuration")
                    continue;

                foreach (var objectDir in Directory.GetDirectories(typeDir))
                    await reader.SendAsync((extension, mdType, objectDir, sourcesRoot, null), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Модули одного объекта EDT: собственные, формы и команды.</summary>
        private void ReadEdtObject(string extension, string sourcesRoot, string mdType, string objectDir, string? externalUrl)
        {
            var objectName = new DirectoryInfo(objectDir).Name;
            var mdoPath = Path.Combine(objectDir, objectName + ".mdo");
            if (!File.Exists(mdoPath))
                return;

            var description = EdtLayout.ReadObject(mdoPath);
            foreach (var moduleFile in EdtLayout.ModulesIn(objectDir))
                CacheModule(moduleFile, extension, description.ObjectId, GetPropertyId(mdType, Path.GetFileNameWithoutExtension(moduleFile)), sourcesRoot, externalUrl);

            CacheEdtChildModules(extension, sourcesRoot, objectDir, "Forms", description.Forms, externalUrl);
            CacheEdtChildModules(extension, sourcesRoot, objectDir, "Commands", description.Commands, externalUrl);
        }

        /// <summary>Модули форм и команд EDT: идентификатор подчинённого объекта берётся из mdo владельца.</summary>
        private void CacheEdtChildModules(
            string extension,
            string sourcesRoot,
            string objectDir,
            string directoryName,
            IReadOnlyDictionary<string, string> ids,
            string? externalUrl)
        {
            var childrenDir = Path.Combine(objectDir, directoryName);
            if (!Directory.Exists(childrenDir))
                return;

            foreach (var childDir in Directory.GetDirectories(childrenDir))
            {
                var childName = new DirectoryInfo(childDir).Name;
                if (!ids.TryGetValue(childName, out var childId))
                {
                    Log.Debug($"{directoryName}: «{childName}» нет в описании объекта, модуль пропущен");
                    continue;
                }

                foreach (var moduleFile in EdtLayout.ModulesIn(childDir))
                    CacheModule(moduleFile, extension, childId, GetPropertyId("", Path.GetFileNameWithoutExtension(moduleFile)), sourcesRoot, externalUrl);
            }
        }

        private void CacheModule(string path, string extension, string objectId, string propertyId, string root, string? externalUrl)
        {
            var normalizedPath = SourcePath.Normalize(path);
            var info = (extension, objectId, propertyId);
            _modulesInfoByPath.TryAdd(normalizedPath, info);
            _pathsByModuleInfo.TryAdd(info, normalizedPath);
            if (externalUrl != null)
            {
                _externalModules.TryAdd(info, externalUrl);
                _externalUrlsByPath[normalizedPath] = externalUrl;
                if (externalUrl.Length > 0)
                    _pathsByExternalUrl[(externalUrl, propertyId)] = normalizedPath;
            }

            // Относительный индекс для кросс-ОС резолва: «<имя каталога корня>/<путь внутри исходного кода>».
            var normalizedRoot = SourcePath.Normalize(root);
            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relKey = Path.GetFileName(normalizedRoot) + "/" + ToForwardSlashes(normalizedPath[normalizedRoot.Length..].TrimStart('\\', '/'));
                _modulesInfoByRelPath.TryAdd(relKey, info);
                _relPathsByModuleInfo.TryAdd(info, relKey);
            }
        }
    }
}
