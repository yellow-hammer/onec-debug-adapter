namespace Onec.DebugAdapter.DebugServer
{
    /// <summary>Сервер отладки отклонил команду; код ответа нужен, чтобы отличать ожидаемые отказы.</summary>
    public sealed class DebugServerException(int statusCode, string message) : InvalidOperationException(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
