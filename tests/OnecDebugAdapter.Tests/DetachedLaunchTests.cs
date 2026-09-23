using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>
    /// Командная строка клиента переходит в промежуточный процесс без потерь:
    /// кавычки строки подключения и пароль автовхода нельзя исказить.
    /// </summary>
    public class DetachedLaunchTests
    {
        [Theory]
        [InlineData(@"/S""srv-1c:1541\erp base"" /N""Иванов"" /P""па""роль""")]
        [InlineData(@"C:\Program Files\1cv8\8.5.1.1343\bin\1cv8c.exe")]
        [InlineData("")]
        public void Аргумент_переживает_передачу(string value)
        {
            Assert.Equal(value, DetachedLaunch.Decode(DetachedLaunch.Encode(value)));
        }

        [Fact]
        public void Закодированный_аргумент_без_кавычек_и_пробелов()
        {
            var encoded = DetachedLaunch.Encode(@"/S""localhost\ib"" /DEBUGGERURL ""http://localhost:1550""");

            Assert.DoesNotContain('"', encoded);
            Assert.DoesNotContain(' ', encoded);
        }
    }
}
