using System;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Onec.DebugAdapter.DebugProtocol;

namespace Onec.DebugAdapter.Services
{
    /// <summary>
    /// Диагностика адаптера (флаг <c>trace</c> конфигурации запуска): событие <c>AdapterLog</c> для клиента
    /// + дубль в stderr. Куда выводить сообщения — решает клиент; незнакомые события DAP-клиенты игнорируют.
    /// </summary>
    internal static class Log
    {
        private static DebugProtocolClient? _client;
        private static bool _enabled;

        public static void Init(DebugProtocolClient client, bool enabled)
        {
            _client = client;
            _enabled = enabled;
        }

        public static void Debug(string message)
        {
            if (!_enabled)
                return;

            try
            {
                Console.Error.WriteLine($"[onec-debug-adapter] {message}");
                _client?.SendEvent(new AdapterLogEvent(message));
            }
            catch
            {
                // диагностика не должна влиять на отладку
            }
        }
    }
}
