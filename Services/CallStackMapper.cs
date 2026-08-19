using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;

namespace Onec.DebugAdapter.Services
{
    /// <summary>
    /// Кадры стека 1С → DAP. Кадр без модуля в исходниках (Выполнить, системный)
    /// остаётся в стеке, но без пути к файлу — иначе один такой кадр валит весь stackTrace.
    /// </summary>
    internal static class CallStackMapper
    {
        internal static string FrameName(StackItemViewInfoData item)
        {
            var presentation = item.Presentation.GetUTF8String();
            return string.IsNullOrEmpty(presentation)
                ? $"строка {item.LineNo}"
                : $"{presentation} : {item.LineNo}";
        }

        internal static string? ResolveSourcePath(IMetadataProvider metadata, BslModuleIdInternal? moduleId)
        {
            if (moduleId == null)
                return null;

            var objectId = moduleId.ObjectId ?? "";
            var propertyId = moduleId.PropertyId ?? "";
            if (objectId.Length == 0 || propertyId.Length == 0)
                return null;

            return metadata.TryModulePathByInfo(moduleId.ExtensionName ?? "", objectId, propertyId);
        }

        internal static StackFrame ToDapFrame(int id, StackItemViewInfoData item, string? sourcePath)
        {
            var frame = new StackFrame
            {
                Id = id,
                Name = FrameName(item),
                Line = (int)item.LineNo
            };
            if (!string.IsNullOrEmpty(sourcePath))
                frame.Source = new Source { Path = sourcePath };
            return frame;
        }
    }
}
