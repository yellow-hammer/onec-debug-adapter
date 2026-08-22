using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;

namespace Onec.DebugAdapter.V8
{
    public interface IMetadataProvider
    {
        Task Init(DebugProtocolClient client, CancellationToken cancellationToken = default);

        string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Путь к модулю по идентификаторам; null, если модуля нет в кэше (кадр <c>Выполнить</c>, системный модуль).
        /// </summary>
        string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default);

        (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string path, CancellationToken cancellationToken = default);

        bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info);

        string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info);

        /// <summary>URL собранного файла по пути исходника.</summary>
        string ExternalModuleUrlByPath(string path);

        /// <summary>Путь исходника по URL собранного файла и свойству модуля.</summary>
        string? TryModulePathByExternalUrl(string url, string propertyId);

        string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info);

        IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info);
    }
}
