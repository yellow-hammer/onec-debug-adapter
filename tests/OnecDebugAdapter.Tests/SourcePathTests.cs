using System.Text.Encodings.Web;
using System.Text.Json;
using Onec.DebugAdapter.Extensions;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>VS Code из Source Control передаёт git-URI вкладки, а не путь на диске.</summary>
    public class SourcePathTests
    {
        [Fact]
        public void ОбычныйПутьНеМеняется()
        {
            var path = Path.Combine("src", "cf", "Ext", "Module.bsl");
            Assert.Equal(path, SourcePath.Resolve(path));
        }

        [Fact]
        public void GitUriДаётПутьИзQuery()
        {
            var filePath = WindowsOrUnix(@"c:\projects\cfg\Ext\ManagerModule.bsl");
            Assert.Equal(filePath, SourcePath.Resolve(GitUri(filePath)));
            Assert.Equal(filePath, SourcePath.Resolve(GitUri(filePath, "~")));
        }

        [Fact]
        public void GitUriССырымJsonВQuery()
        {
            var filePath = WindowsOrUnix(@"c:\projects\cfg\Ext\Module.bsl");
            var json = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["path"] = filePath, ["ref"] = "" },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            Assert.Equal(filePath, SourcePath.Resolve($"git:/unused.bsl?{json}"));
        }

        [Fact]
        public void GitUriБезQueryБерётПутьИзUri()
        {
            Assert.Equal(
                WindowsOrUnix(@"c:\projects\cfg\Ext\Module.bsl"),
                SourcePath.Resolve("git:/c%3A/projects/cfg/Ext/Module.bsl"));
        }

        [Fact]
        public void FileUriДаётЛокальныйПуть()
        {
            var filePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Module.bsl"));
            Assert.Equal(filePath, Path.GetFullPath(SourcePath.Resolve(new Uri(filePath).AbsoluteUri)));
        }

        /// <summary>Каноническим видом адресуются карты путей: у файла и его git-URI он один.</summary>
        [Fact]
        public void КаноническийВидУФайлаИЕгоGitUriОдин()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "cfg", "Ext", "Module.bsl");

            Assert.Equal(SourcePath.Canonical(filePath), SourcePath.Canonical(GitUri(filePath)));
        }

        [Fact]
        public void КаноническийВидДостраиваетОтносительныйПуть()
        {
            var relative = Path.Combine("src", "cf", "Ext", "Module.bsl");

            Assert.Equal(Path.GetFullPath(relative), SourcePath.Canonical(relative));
        }

        /// <summary>Как <c>uri.toString()</c> у Git VS Code и <c>CapitalizeFirstChar</c> в адаптере.</summary>
        internal static string GitUri(string filePath, string refName = "")
        {
            var json = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["path"] = filePath, ["ref"] = refName },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var slashPath = filePath.Replace('\\', '/');
            if (slashPath.Length >= 2 && slashPath[1] == ':')
                slashPath = char.ToLowerInvariant(slashPath[0]) + "%3A" + slashPath[2..];
            if (!slashPath.StartsWith('/'))
                slashPath = "/" + slashPath;

            return $"git:{slashPath}?{Uri.EscapeDataString(json)}".CapitalizeFirstChar();
        }

        private static string WindowsOrUnix(string windowsPath)
            => Path.DirectorySeparatorChar == '\\' ? windowsPath : windowsPath.Replace('\\', '/');
    }
}
