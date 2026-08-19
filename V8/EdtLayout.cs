using System.Xml;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// Чтение исходного кода в формате EDT: описание объекта в <c>&lt;Имя&gt;.mdo</c>, модули рядом
    /// с ним, формы и команды - в подкаталогах, их идентификаторы - в mdo объекта.
    /// </summary>
    public static class EdtLayout
    {
        /// <summary>Каталог исходного кода EDT: там, где лежит Configuration/Configuration.mdo.</summary>
        public static string? FindSourcesRoot(string root)
        {
            foreach (var candidate in new[] { root, Path.Combine(root, "src") })
            {
                if (File.Exists(Path.Combine(candidate, "Configuration", "Configuration.mdo")))
                    return candidate;
            }

            return null;
        }

        /// <summary>Типы объектов, которые EDT держит в отдельном проекте.</summary>
        private static readonly string[] ExternalTypes = { "ExternalDataProcessors", "ExternalReports" };

        /// <summary>
        /// Файлы описания внешних обработок и отчётов: путь может быть проектом EDT
        /// или каталогом с такими проектами.
        /// </summary>
        public static IReadOnlyList<string> ExternalObjects(string path)
        {
            var objects = ExternalObjectsInProject(path);
            if (objects.Count > 0)
                return objects;

            foreach (var child in Directories(path))
                objects.AddRange(ExternalObjectsInProject(child));

            return objects;
        }

        private static List<string> ExternalObjectsInProject(string projectDir)
        {
            var objects = new List<string>();

            foreach (var sourcesRoot in new[] { projectDir, Path.Combine(projectDir, "src") })
                foreach (var type in ExternalTypes)
                    foreach (var objectDir in Directories(Path.Combine(sourcesRoot, type)))
                    {
                        var mdoPath = Path.Combine(objectDir, new DirectoryInfo(objectDir).Name + ".mdo");
                        if (File.Exists(mdoPath))
                            objects.Add(mdoPath);
                    }

            return objects;
        }

        private static IEnumerable<string> Directories(string path)
            => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : Array.Empty<string>();

        /// <summary>Идентификатор объекта: атрибут uuid корневого узла mdo.</summary>
        public static string ObjectId(string mdoPath)
        {
            var xml = new XmlDocument();
            xml.Load(mdoPath);

            return xml.DocumentElement?.Attributes?["uuid"]?.Value
                ?? throw new InvalidOperationException($"В {mdoPath} нет идентификатора объекта");
        }

        /// <summary>Имя конфигурации или расширения из mdo.</summary>
        public static string ConfigurationName(string mdoPath)
        {
            var xml = new XmlDocument();
            xml.Load(mdoPath);

            return xml.DocumentElement?.SelectSingleNode("./*[local-name()='name']")?.InnerText ?? string.Empty;
        }

        /// <summary>Описание объекта из mdo за одно чтение файла.</summary>
        public sealed record ObjectDescription(
            string ObjectId,
            IReadOnlyDictionary<string, string> Forms,
            IReadOnlyDictionary<string, string> Commands);

        /// <summary>
        /// Описание объекта: собственный идентификатор, а также идентификаторы форм и команд,
        /// которые в EDT лежат в mdo владельца.
        /// </summary>
        public static ObjectDescription ReadObject(string mdoPath)
        {
            var forms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var objectId = string.Empty;

            using var reader = XmlReader.Create(mdoPath, ReaderSettings());

            string? childElement = null;
            string? childUuid = null;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.Depth == 0)
                {
                    objectId = reader.GetAttribute("uuid") ?? string.Empty;
                    continue;
                }

                if (reader.Depth == 1 && reader.LocalName is "forms" or "commands")
                {
                    childElement = reader.LocalName;
                    childUuid = reader.GetAttribute("uuid");
                    continue;
                }

                // Имя подчинённого объекта - первый элемент name внутри его узла.
                if (reader.Depth == 2 && reader.LocalName == "name" && childElement != null && childUuid != null)
                {
                    var target = childElement == "forms" ? forms : commands;
                    target.TryAdd(reader.ReadElementContentAsString(), childUuid);
                    childElement = null;
                    childUuid = null;
                }
            }

            if (objectId.Length == 0)
                throw new InvalidOperationException($"В {mdoPath} нет идентификатора объекта");

            return new ObjectDescription(objectId, forms, commands);
        }

        private static XmlReaderSettings ReaderSettings()
            => new() { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };

        /// <summary>Модуль объекта, формы или команды в каталоге; null - модуля нет.</summary>
        public static IEnumerable<string> ModulesIn(string directory)
            => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.bsl", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
    }
}
