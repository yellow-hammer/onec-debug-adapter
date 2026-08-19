using Onec.DebugAdapter.Services;
using System.Xml;

namespace Onec.DebugAdapter.V8
{
    /// <summary>
    /// Внешние обработки и отчёты. Артефакт описан файлом <c>&lt;Имя&gt;.xml</c> рядом с каталогом
    /// объекта (формат конфигуратора) или файлом <c>&lt;Имя&gt;.mdo</c> в каталоге объекта
    /// (формат EDT), где объекты лежат в <c>ExternalDataProcessors</c> и <c>ExternalReports</c>.
    /// </summary>
    public static class ExternalArtifacts
    {
        /// <summary>Типы объектов, которые платформа собирает в отдельные файлы .epf и .erf.</summary>
        private static readonly string[] ExternalTypes = { "ExternalDataProcessor", "ExternalReport" };

        /// <summary>
        /// Описания артефактов: путь может указывать и на артефакт, и на каталог с артефактами.
        /// </summary>
        public static IReadOnlyList<string> Descriptors(string path)
        {
            var descriptors = new List<string>();
            if (!Directory.Exists(path))
                return descriptors;

            descriptors.AddRange(DesignerDescriptors(path));
            foreach (var child in Directory.EnumerateDirectories(path))
                descriptors.AddRange(DesignerDescriptors(child));

            descriptors.AddRange(EdtLayout.ExternalObjects(path));

            return descriptors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> DesignerDescriptors(string directory)
            => Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly).Where(IsExternalObject);

        /// <summary>Тип объекта в описании формата конфигуратора: узел внутри MetaDataObject.</summary>
        private static bool IsExternalObject(string xmlPath)
        {
            try
            {
                using var reader = XmlReader.Create(
                    xmlPath,
                    new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });

                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth == 0)
                        continue;

                    return ExternalTypes.Contains(reader.LocalName);
                }
            }
            catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
            {
                Log.Debug($"не удалось прочитать «{xmlPath}»: {exception.Message}");
            }

            return false;
        }
    }
}
