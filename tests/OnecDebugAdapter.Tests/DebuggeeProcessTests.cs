using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Строка подключения в командной строке клиента 1С.
    /// </summary>
    public class DebuggeeProcessTests
    {
        [Fact]
        public void Путь_с_пробелом_берётся_в_кавычки_после_ключа()
        {
            var arg = DebuggeeProcess.QuoteConnectString(@"/FC:\Проекты\Новая папка\build\ib");

            Assert.Equal(@"/F""C:\Проекты\Новая папка\build\ib""", arg);
        }

        [Fact]
        public void Кавычки_вокруг_всего_токена_не_ставятся()
        {
            var arg = DebuggeeProcess.QuoteConnectString(@"/FC:\Новая папка\ib");

            Assert.False(arg.StartsWith('"'), $"кавычка перед ключом обрезает путь по пробелу: {arg}");
        }

        [Fact]
        public void Серверная_база_тоже_квотируется()
        {
            var arg = DebuggeeProcess.QuoteConnectString(@"/Ssrv-1c:1541\erp base");

            Assert.Equal(@"/S""srv-1c:1541\erp base""", arg);
        }

        [Fact]
        public void Уже_заквотированное_значение_не_удваивается()
        {
            var arg = DebuggeeProcess.QuoteConnectString(@"/F""C:\Новая папка\ib""");

            Assert.Equal(@"/F""C:\Новая папка\ib""", arg);
        }

        [Fact]
        public void Путь_без_пробелов_тоже_в_кавычках_одна_форма_для_всех()
        {
            var arg = DebuggeeProcess.QuoteConnectString(@"/FC:\ib");

            Assert.Equal(@"/F""C:\ib""", arg);
        }

        [Theory]
        [InlineData("")]
        [InlineData("/F")]
        [InlineData("/WS http://host/base")]
        public void Незнакомое_или_пустое_остаётся_как_есть(string connect)
        {
            Assert.Equal(connect.Trim(), DebuggeeProcess.QuoteConnectString(connect));
        }
    }
}
