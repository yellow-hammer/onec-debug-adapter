using System.Runtime.CompilerServices;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Тесты держат в одном процессе оба конца DAP: библиотека протокола читает поток синхронно
    /// на потоках пула, SendRequestSync тоже занимает поток. Пул добавляет потоки по одному
    /// раз в полсекунды, и первый запрос на раннере с малым числом ядер ждёт секундами.
    /// </summary>
    internal static class ThreadPoolSetup
    {
        [ModuleInitializer]
        internal static void Init()
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            ThreadPool.SetMinThreads(Math.Max(worker, 32), io);
        }
    }
}
