using System.Text;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Onec.DebugAdapter.DebugServer;
using Onec.DebugAdapter.Services;
using Onec.DebugAdapter.V8;
using Xunit;

namespace Onec.DebugAdapter.Tests
{
    /// <summary>Кадр Выполнить и другие модули вне исходников не должны валить весь стек.</summary>
    public class CallStackMapperTests
    {
        [Fact]
        public void ИмяКадраБерётсяИзPresentation()
        {
            var item = Frame("Выполнить", 7);

            Assert.Equal("Выполнить : 7", CallStackMapper.FrameName(item));
        }

        [Fact]
        public void ПустойPresentationДаётИмяПоСтроке()
        {
            var item = new StackItemViewInfoData { LineNo = 3 };

            Assert.Equal("строка 3", CallStackMapper.FrameName(item));
        }

        [Fact]
        public void КадрБезПутиНеИмеетSource()
        {
            var frame = CallStackMapper.ToDapFrame(1, Frame("Выполнить", 1), null);

            Assert.Null(frame.Source);
            Assert.Equal("Выполнить : 1", frame.Name);
            Assert.Equal(1, frame.Line);
        }

        [Fact]
        public void КадрСПутёмПолучаетSource()
        {
            var frame = CallStackMapper.ToDapFrame(2, Frame("Модуль", 14), @"C:\cf\Module.bsl");

            Assert.Equal(@"C:\cf\Module.bsl", frame.Source.Path);
        }

        [Fact]
        public void ПутьНеИщетсяБезModuleId()
        {
            Assert.Null(CallStackMapper.ResolveSourcePath(new StubMetadata("C:/x.bsl"), null));
        }

        [Fact]
        public void ПутьНеИщетсяБезИдентификаторовМодуля()
        {
            var moduleId = new BslModuleIdInternal { ObjectId = "", PropertyId = "" };

            Assert.Null(CallStackMapper.ResolveSourcePath(new StubMetadata("C:/x.bsl"), moduleId));
        }

        [Fact]
        public void НеизвестныйМодульНеДаётПуть()
        {
            var moduleId = new BslModuleIdInternal
            {
                ObjectId = "00000000-0000-0000-0000-000000000000",
                PropertyId = "11111111-1111-1111-1111-111111111111"
            };

            Assert.Null(CallStackMapper.ResolveSourcePath(new StubMetadata(null), moduleId));
        }

        [Fact]
        public void ИзвестныйМодульДаётПуть()
        {
            var moduleId = new BslModuleIdInternal
            {
                ObjectId = "00000000-0000-0000-0000-000000000001",
                PropertyId = "11111111-1111-1111-1111-111111111111"
            };

            Assert.Equal("C:/known.bsl", CallStackMapper.ResolveSourcePath(new StubMetadata("C:/known.bsl"), moduleId));
        }

        private static StackItemViewInfoData Frame(string presentation, int line)
            => new()
            {
                LineNo = line,
                Presentation = Encoding.UTF8.GetBytes(presentation)
            };

        private sealed class StubMetadata(string? path) : IMetadataProvider
        {
            public Task Init(DebugProtocolClient client, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public string ModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => path ?? throw new KeyNotFoundException();
            public string? TryModulePathByInfo(string extension, string objectId, string propertyId, CancellationToken cancellationToken = default)
                => path;
            public (string Extension, string ObjectId, string PropertyId) ModuleInfoByPath(string modulePath, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public bool IsExternalModule((string Extension, string ObjectId, string PropertyId) info) => false;
            public string ExternalModuleUrl((string Extension, string ObjectId, string PropertyId) info) => "";
            public string? LocalModulePath((string Extension, string ObjectId, string PropertyId) info) => path;
            public IEnumerable<(string Extension, string ObjectId, string PropertyId)> ExtensionCounterparts((string Extension, string ObjectId, string PropertyId) info)
                => [];
        }
    }
}
